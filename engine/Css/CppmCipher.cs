// SPDX-License-Identifier: GPL-2.0-or-later
// Port to C# of the CPPM (DVD-Audio) part of libdvdcss src/libdvdcpxm.c
// (Copyright (C) Maxim V. Anisiutkin, (C) 2025 Saifelden Ismail, VideoLAN; GPL-2.0-or-later),
// at libdvdcss commit 811ce97. Functions ported: c2_init, F, c2_enc, c2_dec, c2_g, c2_dcbc,
// process_mkb, mpeg2_check_pes_scrambling_control, mpeg2_reset_pes_scrambling_control,
// mpeg2_reset_cci, cppm_decrypt_block, and the CPPM device keys. As in CssCipher.cs, the code
// follows the C original line by line. The C code reads 64-bit values from the disc big-endian
// (READ64_BE / B2N_64); here that is Bytes-style BE64 reads and writes.
// CPRM (recordable media) is not ported: it needs its own drive commands and DVD-VR handling.

using xISOBurn.Util;

namespace xISOBurn.Css
{
    internal static class CppmCipher
    {
        public const int BlockSize = 2048;
        /// <summary>Only the last 1920 bytes of a block are encrypted; the first 128 are in the clear.</summary>
        public const int EncryptedSize = 1920;
        /// <summary>Size of the CPPM media key block libdvdread passes in (CPRM_MKB_SIZE).</summary>
        public const int MkbSize = 16 * 24576 - 16;
        private const byte CciByte = 0x00;

        private static readonly byte[] Sbox =
        {
            0x3a, 0xd0, 0x9a, 0xb6, 0xf5, 0xc1, 0x16, 0xb7, 0x58, 0xf6, 0xed, 0xe6, 0xd9, 0x8c, 0x57, 0xfc,
            0xfd, 0x4b, 0x9b, 0x47, 0x0e, 0x8e, 0xff, 0xf3, 0xbb, 0xba, 0x0a, 0x80, 0x15, 0xd7, 0x2b, 0x36,
            0x6a, 0x43, 0x5a, 0x89, 0xb4, 0x5d, 0x71, 0x19, 0x8f, 0xa0, 0x88, 0xb8, 0xe8, 0x8a, 0xc3, 0xae,
            0x7c, 0x4e, 0x3d, 0xb5, 0x96, 0xcc, 0x21, 0x00, 0x1a, 0x6b, 0x12, 0xdb, 0x1f, 0xe4, 0x11, 0x9d,
            0xd3, 0x93, 0x68, 0xb0, 0x7f, 0x3b, 0x52, 0xb9, 0x94, 0xdd, 0xa5, 0x1b, 0x46, 0x60, 0x31, 0xec,
            0xc9, 0xf8, 0xe9, 0x5e, 0x13, 0x98, 0xbf, 0x27, 0x56, 0x08, 0x91, 0xe3, 0x6f, 0x20, 0x40, 0xb2,
            0x2c, 0xce, 0x02, 0x10, 0xe0, 0x18, 0xd5, 0x6c, 0xde, 0xcd, 0x87, 0x79, 0xaf, 0xa9, 0x26, 0x50,
            0xf2, 0x33, 0x92, 0x6e, 0xc0, 0x3f, 0x39, 0x41, 0xaa, 0x5b, 0x7d, 0x24, 0x03, 0xd6, 0x2f, 0xeb,
            0x0b, 0x99, 0x86, 0x4c, 0x51, 0x45, 0x8d, 0x2e, 0xef, 0x07, 0x7b, 0xe2, 0x4d, 0x7a, 0xfe, 0x25,
            0x5c, 0x29, 0xa2, 0xa8, 0xb1, 0xf0, 0xb3, 0xc4, 0x30, 0x7e, 0x63, 0x38, 0xcb, 0xf4, 0x4f, 0xd1,
            0xdf, 0x44, 0x32, 0xdc, 0x17, 0x5f, 0x66, 0x2a, 0x81, 0x9e, 0x77, 0x4a, 0x65, 0x67, 0x34, 0xfa,
            0x54, 0x1e, 0x14, 0xbe, 0x04, 0xf1, 0xa7, 0x9c, 0x8b, 0x37, 0xee, 0x85, 0xab, 0x22, 0x0f, 0x69,
            0xc5, 0xd4, 0x05, 0x84, 0xa4, 0x73, 0x42, 0xa1, 0x64, 0xe1, 0x70, 0x83, 0x90, 0xc2, 0x48, 0x0d,
            0x61, 0x1c, 0xc6, 0x72, 0xfb, 0x76, 0x74, 0xe7, 0x01, 0xd8, 0xc8, 0xd2, 0x75, 0xa3, 0xcf, 0x28,
            0x82, 0x1d, 0x49, 0x35, 0xc7, 0xbd, 0xca, 0xa6, 0xac, 0x0c, 0x62, 0xad, 0xf9, 0x3c, 0xea, 0x2d,
            0x59, 0xda, 0x3e, 0x97, 0x6d, 0x09, 0xf7, 0x55, 0xe5, 0x23, 0x53, 0x9f, 0x06, 0xbc, 0x95, 0x78,
        };

        private struct DeviceKey
        {
            public byte Col;
            public ushort Row;
            public ulong Key;
            public DeviceKey(byte col, ushort row, ulong key) { Col = col; Row = row; Key = key; }
        }

        /// <summary>cppm_device_keys: the DVD-Audio player keys of libdvdcss, in its order.</summary>
        private static readonly DeviceKey[] DeviceKeys =
        {
            new DeviceKey(0x00, 0x4821, 0x6d05086b755c81), new DeviceKey(0x01, 0x091c, 0x97ace18dd26973),
            new DeviceKey(0x02, 0x012a, 0xfefc0a25a38d42), new DeviceKey(0x03, 0x469b, 0x0780491970db2c),
            new DeviceKey(0x04, 0x0f9b, 0x0bedd116d43484), new DeviceKey(0x05, 0x59b2, 0x566936bcebe294),
            new DeviceKey(0x06, 0x5fc8, 0xdc610f649b1fc0), new DeviceKey(0x07, 0x11de, 0x6ee01d3872c2d9),
            new DeviceKey(0x08, 0x52b6, 0xd0132c376e439b), new DeviceKey(0x09, 0x135f, 0x800faa66206922),
            new DeviceKey(0x0a, 0x3806, 0x9d1aa1460885c2), new DeviceKey(0x0b, 0x2da2, 0x9833f21818ba33),
            new DeviceKey(0x0c, 0x113f, 0xd50aa7d022045a), new DeviceKey(0x0d, 0x11ec, 0x88abee7bb83a32),
            new DeviceKey(0x0e, 0x071b, 0x9b45eea4e7d140), new DeviceKey(0x0f, 0x5c55, 0x5a49f860cca5cf),

            new DeviceKey(0x00, 0x0375, 0x1a12793404c279), new DeviceKey(0x01, 0x4307, 0x61418b44cea550),
            new DeviceKey(0x02, 0x1f70, 0x52bde5b73adcda), new DeviceKey(0x03, 0x1bbc, 0x70a031ae493159),
            new DeviceKey(0x04, 0x1f9d, 0x0a570636aedb61), new DeviceKey(0x05, 0x4e7b, 0xc313563e7883e9),
            new DeviceKey(0x06, 0x07c4, 0x32c55f7bc42d45), new DeviceKey(0x07, 0x4216, 0x4f854df6c1d721),
            new DeviceKey(0x08, 0x11c5, 0xc0e3f0f3df33cc), new DeviceKey(0x09, 0x0486, 0xbfca7754db5de6),
            new DeviceKey(0x0a, 0x2f82, 0xa964fc061af87c), new DeviceKey(0x0b, 0x236a, 0xb96d68856c45d5),
            new DeviceKey(0x0c, 0x5beb, 0xd2ca3cbb7d13cc), new DeviceKey(0x0d, 0x3db6, 0x58cf827ff3c540),
            new DeviceKey(0x0e, 0x4b22, 0xbb4037442a869c), new DeviceKey(0x0f, 0x59b5, 0x3a83e0ddf37a6e),
        };

        // ------------------------------------------------------------------ C2 cipher

        private static readonly uint[] SboxF = MakeSboxF();

        private static uint Rol32(uint code, int n) { return (code << n) | (code >> (32 - n)); }
        private static byte Rol8(byte code, int n) { return (byte)((code << n) | (code >> (8 - n))); }

        /// <summary>c2_init.</summary>
        private static uint[] MakeSboxF()
        {
            var t = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                uint c0 = Sbox[i];
                uint c1 = Rol8((byte)(c0 ^ 0x65), 1);
                uint c2 = Rol8((byte)(c0 ^ 0x2b), 5);
                uint c3 = Rol8((byte)(c0 ^ 0xc9), 2);
                c0 ^= (uint)i;
                t[i] = (c3 << 24) + (c2 << 16) + (c1 << 8) + c0;
            }
            return t;
        }

        private static uint F(uint code, uint key)
        {
            uint work = code + key;
            work ^= SboxF[work & 0xff];
            work ^= Rol32(work, 9) ^ Rol32(work, 22);
            return work;
        }

        /// <summary>The 10 round keys of a 56-bit key (the loop shared by c2_enc, c2_dec and the CBC modes).</summary>
        private static void SubKeys(ulong key, uint[] sk, int rounds)
        {
            uint ktmpa = (uint)((key >> 32) & 0x00ffffff);
            uint ktmpb = (uint)(key & 0xffffffff);
            for (int round = 0; round < rounds; round++)
            {
                ktmpa &= 0x00ffffff;
                sk[round] = ktmpb + ((uint)Sbox[(ktmpa & 0xff) ^ (uint)round] << 4);
                uint ktmpc = ktmpb >> (32 - 17);
                uint ktmpd = ktmpa >> (24 - 17);
                ktmpa = (ktmpa << 17) | ktmpc;
                ktmpb = (ktmpb << 17) | ktmpd;
            }
        }

        private static ulong C2Enc(ulong code, ulong key)
        {
            uint L = (uint)((code >> 32) & 0xffffffff), R = (uint)(code & 0xffffffff), t;
            var sk = new uint[10];
            SubKeys(key, sk, 10);
            for (int round = 0; round < 10; round++)
            {
                L += F(R, sk[round]);
                t = L; L = R; R = t;
            }
            t = L; L = R; R = t;
            return ((ulong)L << 32) | R;
        }

        private static ulong C2Dec(ulong code, ulong key)
        {
            uint L = (uint)((code >> 32) & 0xffffffff), R = (uint)(code & 0xffffffff), t;
            var sk = new uint[10];
            SubKeys(key, sk, 10);
            for (int round = 9; round >= 0; round--)
            {
                L -= F(R, sk[round]);
                t = L; L = R; R = t;
            }
            t = L; L = R; R = t;
            return ((ulong)L << 32) | R;
        }

        private static ulong C2G(ulong code, ulong key) { return C2Enc(code, key) ^ code; }

        /// <summary>c2_dcbc: decrypts buf[offset..offset+length] in place (C2 in its CBC-like mode).</summary>
        private static void C2Dcbc(byte[] buf, int offset, ulong key, int length)
        {
            var sk = new uint[10];
            ulong inkey = key;
            int keyRound = 10;
            for (int i = 0; i < length; i += 8)
            {
                int p = offset + i;
                ulong inout = BE64(buf, p);
                uint L = (uint)((inout >> 32) & 0xffffffff), R = (uint)(inout & 0xffffffff), t;
                SubKeys(inkey, sk, keyRound);
                for (int round = 9; round >= 0; round--)
                {
                    L -= F(R, sk[round % keyRound]);
                    t = L; L = R; R = t;
                    if (round == 5)
                        inkey = key ^ (((ulong)(R & 0x00ffffff) << 32) | L);
                }
                t = L; L = R; R = t;
                inout = ((ulong)L << 32) | R;
                PutBE64(buf, p, inout);
                keyRound = 2;
            }
        }

        private static ulong BE64(byte[] b, int o)
        {
            return ((ulong)Bytes.BE32(b, o) << 32) | Bytes.BE32(b, o + 4);
        }

        private static void PutBE64(byte[] b, int o, ulong v)
        {
            Bytes.PutBE32(b, o, (uint)(v >> 32));
            Bytes.PutBE32(b, o + 4, (uint)v);
        }

        // ------------------------------------------------------------------ media key block

        /// <summary>
        /// process_mkb with the CPPM device keys: walks the records of the media key block
        /// (AUDIO_TS/DVDAUDIO.MKB) until a device key yields a media key that the verification record
        /// confirms. Returns false if none does.
        /// </summary>
        public static bool ProcessMkb(byte[] mkb, int mkbLen, out ulong mediaKey)
        {
            int iDevKey = 0;
            bool noMoreKeys = false;
            ulong buffer = 0, verificationData = 0;
            mediaKey = 0;

            while (!noMoreKeys && iDevKey < DeviceKeys.Length)
            {
                // Skip the file identifier and the length.
                int mkbPos = 16;
                bool noMoreRecords = false;
                while (!noMoreRecords)
                {
                    if (mkbPos < 0 || mkbPos + 4 > mkbLen) break;

                    byte recordType = mkb[mkbPos];
                    // The record length: 24 bits big-endian after the type byte.
                    int length = (mkb[mkbPos + 1] << 16) | (mkb[mkbPos + 2] << 8) | mkb[mkbPos + 3];

                    // 'buffer' holds bytes 4..11 of the record as a big-endian value.
                    if (length >= 12)
                    {
                        if (mkbPos + 12 > mkbLen) break;
                        buffer = BE64(mkb, mkbPos + 4);
                    }
                    else if (length < 4)
                    {
                        length = 4;
                    }

                    bool calculate = false;
                    switch (recordType)
                    {
                        case 0x82: // conditionally calculate media key record
                            buffer = C2Dec(buffer, mediaKey);
                            if ((buffer & 0xffffffff00000000UL) == 0xdeadbeef00000000UL) calculate = true;
                            break;
                        case 0x01: // calculate media key record
                            calculate = true;
                            break;
                        case 0x02: // end of media key record
                            noMoreRecords = true;
                            break;
                        case 0x81: // verify media key record
                            verificationData = buffer;
                            break;
                    }

                    if (calculate)
                    {
                        // The column is byte 4 of the (big-endian) value: record byte 8.
                        byte column = (byte)(buffer >> 24);
                        // Get the appropriate device key for the column.
                        noMoreKeys = true;
                        for (int i = iDevKey; i < DeviceKeys.Length; i++)
                        {
                            if (DeviceKeys[i].Col == column)
                            {
                                noMoreKeys = false;
                                iDevKey = i;
                                break;
                            }
                        }
                        if (!noMoreKeys)
                        {
                            var dk = DeviceKeys[iDevKey];
                            int at = mkbPos + 12 + dk.Row * 8;
                            if (12 + dk.Row * 8 + 8 <= length && at + 8 <= mkbLen)
                            {
                                buffer = BE64(mkb, at);
                                if (recordType == 0x82) buffer = C2Dec(buffer, mediaKey);
                                mediaKey = (C2Dec(buffer, dk.Key) & 0x00ffffffffffffffUL) ^ (((ulong)column << 32) | dk.Row);
                                buffer = C2Dec(verificationData, mediaKey);
                                if ((buffer & 0xffffffff00000000UL) == 0xdeadbeef00000000UL)
                                    return true;
                            }
                        }
                    }
                    mkbPos += length;
                }
                iDevKey++;
            }
            mediaKey = 0;
            return false;
        }

        // ------------------------------------------------------------------ blocks

        private static bool IsSyncCode(byte[] b, int o)
        {
            return b[o] == 0x00 && b[o + 1] == 0x00 && b[o + 2] == 0x01 && b[o + 3] == 0xBA;
        }

        /// <summary>mpeg2_check_pes_scrambling_control: true if the pack says it is encrypted.</summary>
        public static bool IsEncrypted(byte[] b, int o)
        {
            return IsSyncCode(b, o) && ((b[o + 20] & 0x30) >> 4) != 0;
        }

        /// <summary>mpeg2_reset_cci: sets the copy control byte of the PCM / MLP audio packets to "copy freely".</summary>
        private static void ResetCci(byte[] b, int o)
        {
            const int PackHeaderSize = 14, PesHeaderSize = 6, PesPayloadOffset = 9, AudioSubheaderSize = 13;
            int end = o + BlockSize;
            if (!IsSyncCode(b, o)) return;
            int p = o + PackHeaderSize + (b[o + 13] & 0x07);
            while (p + PesHeaderSize <= end)
            {
                int pesLen = (b[p + 4] << 8) + b[p + 5];
                if (b[p] != 0x00 || b[p + 1] != 0x00 || b[p + 2] != 0x01) break;
                int sid = b[p + 3];
                if (sid == 0xbd && p + PesPayloadOffset <= end) // private stream 1
                {
                    int a = p + PesPayloadOffset + b[p + 8];
                    if (a + AudioSubheaderSize <= end)
                    {
                        switch (b[a])
                        {
                            case 0xa0: if (b[a + 3] > 8) b[a + 12] = CciByte; break; // PCM
                            case 0xa1: if (b[a + 3] > 4) b[a + 8] = CciByte; break;  // MLP
                        }
                    }
                }
                p += PesHeaderSize + pesLen;
            }
        }

        /// <summary>
        /// cppm_decrypt_block with DVDCPXM_RESET_CCI (what dvdcpxm_read does): decrypts the 2048-byte
        /// block at b[o] in place, clears its scrambling bits and resets the CCI. True if it was encrypted.
        /// </summary>
        public static bool DecryptBlock(byte[] b, int o, ulong albumId, ulong mediaKey)
        {
            bool encrypted = false;
            if (IsEncrypted(b, o))
            {
                ulong kAu = C2G(albumId, mediaKey) & 0x00ffffffffffffffUL;
                ulong kI = C2G(BE64(b, o + 24), kAu) & 0x00ffffffffffffffUL;
                kI = C2G(BE64(b, o + 32), kI) & 0x00ffffffffffffffUL;
                kI = C2G(BE64(b, o + 40), kI) & 0x00ffffffffffffffUL;
                kI = C2G(BE64(b, o + 48), kI) & 0x00ffffffffffffffUL;
                ulong kC = C2G(BE64(b, o + 84), kI) & 0x00ffffffffffffffUL;
                C2Dcbc(b, o + BlockSize - EncryptedSize, kC, EncryptedSize);
                b[o + 20] &= 0xCD; // mpeg2_reset_pes_scrambling_control: scrambling and copyright bits
                encrypted = true;
            }
            ResetCci(b, o);
            return encrypted;
        }

        /// <summary>The album ID: 8 bytes at offset 80 of the disc key block (after the bus key XOR).</summary>
        public static ulong AlbumId(byte[] discKeyBlock) { return BE64(discKeyBlock, 80); }
    }
}
