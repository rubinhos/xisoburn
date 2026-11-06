// Port to C# of the CSS cipher from libdvdcss src/css.c
// (Copyright (C) 1999-2008 VideoLAN, GPL-2.0-or-later; authors Stéphane Borel, Håkan Hjort; based on
// css-auth by Derek Fawcus, the CSS ioctls example by Andrew T. Veliath, DeCSSPlus and DecVOB).
// Functions ported: CryptKey, DecryptKey, DecryptDiscKey (player keys), DecryptTitleKey and
// dvdcss_unscramble. The code follows the C original line by line on purpose, so it can be checked
// against it; byte truncations that C does implicitly (uint8_t variables) are explicit here.

using xISOBurn.Util;
using T = xISOBurn.Css.CssTables;

namespace xISOBurn.Css
{
    internal static class CssCipher
    {
        public const int KeySize = 5;
        public const int DiscKeySize = 2048;
        public const int SectorSize = 2048;

        // ------------------------------------------------------------------ authentication (CryptKey)

        private static readonly byte[,] PermChallenge =
        {
            { 1, 3, 0, 7, 5, 2, 9, 6, 4, 8 },
            { 6, 1, 9, 3, 8, 5, 7, 4, 0, 2 },
            { 4, 0, 3, 5, 7, 2, 8, 6, 1, 9 },
        };

        private static readonly byte[,] PermVariant =
        {
            { 0x0a, 0x08, 0x0e, 0x0c, 0x0b, 0x09, 0x0f, 0x0d,
              0x1a, 0x18, 0x1e, 0x1c, 0x1b, 0x19, 0x1f, 0x1d,
              0x02, 0x00, 0x06, 0x04, 0x03, 0x01, 0x07, 0x05,
              0x12, 0x10, 0x16, 0x14, 0x13, 0x11, 0x17, 0x15 },
            { 0x12, 0x1a, 0x16, 0x1e, 0x02, 0x0a, 0x06, 0x0e,
              0x10, 0x18, 0x14, 0x1c, 0x00, 0x08, 0x04, 0x0c,
              0x13, 0x1b, 0x17, 0x1f, 0x03, 0x0b, 0x07, 0x0f,
              0x11, 0x19, 0x15, 0x1d, 0x01, 0x09, 0x05, 0x0d },
        };

        private static readonly byte[] Variants =
        {
            0xB7, 0x74, 0x85, 0xD0, 0xCC, 0xDB, 0xCA, 0x73,
            0x03, 0xFE, 0x31, 0x03, 0x52, 0xE0, 0xB7, 0x42,
            0x63, 0x16, 0xF2, 0x2A, 0x79, 0x52, 0xFF, 0x1B,
            0x7A, 0x11, 0xCA, 0x1A, 0x9B, 0x40, 0xAD, 0x01,
        };

        private static readonly byte[] Secret = { 0x55, 0xD6, 0xC4, 0xC5, 0x28 };

        /// <summary>
        /// CryptKey: computes key1 (keyType 0), key2 (1) or the bus key (2) from a 10-byte challenge.
        /// variant: 0..31. Result: 5 bytes in key.
        /// </summary>
        public static void CryptKey(int keyType, int variant, byte[] challenge, byte[] key)
        {
            var scratch = new byte[10];
            var bits = new byte[30];
            var tmp1 = new byte[5];
            var tmp2 = new byte[5];

            for (int i = 9; i >= 0; --i)
                scratch[i] = challenge[PermChallenge[keyType, i]];

            int cssVariant = keyType == 0 ? variant : PermVariant[keyType - 1, variant];

            for (int i = 4; i >= 0; --i)
                tmp1[i] = (byte)(scratch[5 + i] ^ Secret[i] ^ T.CryptTab2[i]);

            uint lfsr0 = ((uint)tmp1[0] << 17) | ((uint)tmp1[1] << 9) | (((uint)tmp1[2] & ~7u) << 1) | 8u | ((uint)tmp1[2] & 7u);
            uint lfsr1 = ((uint)tmp1[3] << 9) | 0x100u | tmp1[4];

            int index = bits.Length;
            int carry = 0;
            do
            {
                int val = 0;
                for (int bit = 0; bit < 8; ++bit)
                {
                    uint o0 = ((lfsr0 >> 24) ^ (lfsr0 >> 21) ^ (lfsr0 >> 20) ^ (lfsr0 >> 12)) & 1u;
                    lfsr0 = (lfsr0 << 1) | o0;

                    uint o1 = ((lfsr1 >> 16) ^ (lfsr1 >> 2)) & 1u;
                    lfsr1 = (lfsr1 << 1) | o1;

                    int combined = (o1 == 0 ? 1 : 0) + carry + (o0 == 0 ? 1 : 0);
                    carry = (combined >> 1) & 1;
                    val |= (combined & 1) << bit;
                }
                bits[--index] = (byte)val;
            } while (index > 0);

            int cse = Variants[cssVariant] ^ T.CryptTab2[cssVariant];

            Round(bits, 25, scratch, tmp1, cse, false); tmp1[4] ^= tmp1[0];
            Round(bits, 20, tmp1, tmp2, cse, false);    tmp2[4] ^= tmp2[0];
            Round(bits, 15, tmp2, tmp1, cse, true);     tmp1[4] ^= tmp1[0];
            Round(bits, 10, tmp1, tmp2, cse, true);     tmp2[4] ^= tmp2[0];
            Round(bits, 5, tmp2, tmp1, cse, false);     tmp1[4] ^= tmp1[0];
            Round(bits, 0, tmp1, key, cse, false);
        }

        /// <summary>
        /// One of the six 40-bit blocks of CryptKey. "term" is always the input byte of the previous
        /// index (in C: the increment expression of the for loop). The "extra" blocks (3rd and 4th in
        /// the C code) pass the result through tab0/tab2 once more.
        /// </summary>
        private static void Round(byte[] bits, int bitOffset, byte[] input, byte[] output, int cse, bool extra)
        {
            int term = 0;
            for (int i = 4; i >= 0; --i)
            {
                int idx = bits[bitOffset + i] ^ input[i];
                idx = (T.CryptTab1[idx] ^ ~T.CryptTab2[idx] ^ cse) & 0xff;
                int v = (T.CryptTab2[idx] ^ T.CryptTab3[idx] ^ term) & 0xff;
                if (extra) v = T.CryptTab0[v] ^ T.CryptTab2[v];
                output[i] = (byte)v;
                term = input[i];
            }
        }

        // ------------------------------------------------------------------ key decryption

        /// <summary>
        /// DecryptKey: decrypts 5 bytes (crypted[cryptedOffset..]) with a 5-byte key.
        /// invert = 0x00 for disc keys, 0xff for title keys.
        /// </summary>
        public static void DecryptKey(byte invert, byte[] key, byte[] crypted, int cryptedOffset, byte[] result)
        {
            // Copy the input first: in C the title key is decrypted in place.
            var c = new byte[5];
            for (int i = 0; i < 5; i++) c[i] = crypted[cryptedOffset + i];

            uint lfsr1Lo = key[0] | 0x100u;
            uint lfsr1Hi = key[1];

            uint lfsr0 = (((uint)key[4] << 17) | ((uint)key[3] << 9) | ((uint)key[2] << 1)) + 8u - (uint)(key[2] & 7);
            lfsr0 = ((uint)T.CssTab4[lfsr0 & 0xff] << 24) |
                    ((uint)T.CssTab4[(lfsr0 >> 8) & 0xff] << 16) |
                    ((uint)T.CssTab4[(lfsr0 >> 16) & 0xff] << 8) |
                    T.CssTab4[(lfsr0 >> 24) & 0xff];

            var k = new byte[5];
            uint combined = 0;
            for (int i = 0; i < KeySize; ++i)
            {
                uint o1 = (uint)(T.CssTab2[lfsr1Hi] ^ T.CssTab3[lfsr1Lo]);
                lfsr1Hi = lfsr1Lo >> 1;
                lfsr1Lo = ((lfsr1Lo & 1) << 8) ^ o1;
                o1 = T.CssTab4[o1];

                uint o0 = (((((((lfsr0 >> 8) ^ lfsr0) >> 1) ^ lfsr0) >> 3) ^ lfsr0) >> 7) & 0xff;
                lfsr0 = (lfsr0 >> 8) | (o0 << 24);

                combined += (o0 ^ invert) + o1;
                k[i] = (byte)combined;
                combined >>= 8;
            }

            var r = new byte[5];
            r[4] = (byte)(k[4] ^ T.CssTab1[c[4]] ^ c[3]);
            r[3] = (byte)(k[3] ^ T.CssTab1[c[3]] ^ c[2]);
            r[2] = (byte)(k[2] ^ T.CssTab1[c[2]] ^ c[1]);
            r[1] = (byte)(k[1] ^ T.CssTab1[c[1]] ^ c[0]);
            r[0] = (byte)(k[0] ^ T.CssTab1[c[0]] ^ r[4]);

            r[4] = (byte)(k[4] ^ T.CssTab1[r[4]] ^ r[3]);
            r[3] = (byte)(k[3] ^ T.CssTab1[r[3]] ^ r[2]);
            r[2] = (byte)(k[2] ^ T.CssTab1[r[2]] ^ r[1]);
            r[1] = (byte)(k[1] ^ T.CssTab1[r[1]] ^ r[0]);
            r[0] = (byte)(k[0] ^ T.CssTab1[r[0]]);

            for (int i = 0; i < 5; i++) result[i] = r[i];
        }

        /// <summary>
        /// Alternate DVD player keys, generated with Frank A. Stevenson's PlayerKey cracker
        /// (same list as libdvdcss).
        /// </summary>
        private static readonly byte[][] PlayerKeys =
        {
            new byte[] { 0x01, 0xaf, 0xe3, 0x12, 0x80 },
            new byte[] { 0x12, 0x11, 0xca, 0x04, 0x3b },
            new byte[] { 0x14, 0x0c, 0x9e, 0xd0, 0x09 },
            new byte[] { 0x14, 0x71, 0x35, 0xba, 0xe2 },
            new byte[] { 0x1a, 0xa4, 0x33, 0x21, 0xa6 },
            new byte[] { 0x26, 0xec, 0xc4, 0xa7, 0x4e },
            new byte[] { 0x2c, 0xb2, 0xc1, 0x09, 0xee },
            new byte[] { 0x2f, 0x25, 0x9e, 0x96, 0xdd },
            new byte[] { 0x33, 0x2f, 0x49, 0x6c, 0xe0 },
            new byte[] { 0x35, 0x5b, 0xc1, 0x31, 0x0f },
            new byte[] { 0x36, 0x67, 0xb2, 0xe3, 0x85 },
            new byte[] { 0x39, 0x3d, 0xf1, 0xf1, 0xbd },
            new byte[] { 0x3b, 0x31, 0x34, 0x0d, 0x91 },
            new byte[] { 0x45, 0xed, 0x28, 0xeb, 0xd3 },
            new byte[] { 0x48, 0xb7, 0x6c, 0xce, 0x69 },
            new byte[] { 0x4b, 0x65, 0x0d, 0xc1, 0xee },
            new byte[] { 0x4c, 0xbb, 0xf5, 0x5b, 0x23 },
            new byte[] { 0x51, 0x67, 0x67, 0xc5, 0xe0 },
            new byte[] { 0x53, 0x94, 0xe1, 0x75, 0xbf },
            new byte[] { 0x57, 0x2c, 0x8b, 0x31, 0xae },
            new byte[] { 0x63, 0xdb, 0x4c, 0x5b, 0x4a },
            new byte[] { 0x7b, 0x1e, 0x5e, 0x2b, 0x57 },
            new byte[] { 0x85, 0xf3, 0x85, 0xa0, 0xe0 },
            new byte[] { 0xab, 0x1e, 0xe7, 0x7b, 0x72 },
            new byte[] { 0xab, 0x36, 0xe3, 0xeb, 0x76 },
            new byte[] { 0xb1, 0xb8, 0xf9, 0x38, 0x03 },
            new byte[] { 0xb8, 0x5d, 0xd8, 0x53, 0xbd },
            new byte[] { 0xbf, 0x92, 0xc3, 0xb0, 0xe2 },
            new byte[] { 0xcf, 0x1a, 0xb2, 0xf8, 0x0a },
            new byte[] { 0xec, 0xa0, 0xcf, 0xb3, 0xff },
            new byte[] { 0xfc, 0x95, 0xa9, 0x87, 0x35 },
        };

        /// <summary>
        /// DecryptDiscKey: tries every player key at every one of the 408 positions of the disc key
        /// block (already XORed with the bus key). The first 5 bytes are the disc key encrypted with
        /// itself, which tells when the right pair was found. Returns the player key index, or -1.
        /// </summary>
        public static int DecryptDiscKey(byte[] structDiscKey, byte[] discKey)
        {
            var verify = new byte[5];
            for (int n = 0; n < PlayerKeys.Length; n++)
            {
                for (int i = 1; i < 409; i++)
                {
                    DecryptKey(0, PlayerKeys[n], structDiscKey, 5 * i, discKey);
                    DecryptKey(0, discKey, structDiscKey, 0, verify);
                    if (Bytes.Equal(discKey, 0, verify, 0, 5)) return n;
                }
            }
            for (int i = 0; i < 5; i++) discKey[i] = 0;
            return -1;
        }

        /// <summary>DecryptTitleKey: decrypts the title key with the disc key (in place).</summary>
        public static void DecryptTitleKey(byte[] discKey, byte[] titleKey)
        {
            DecryptKey(0xff, discKey, titleKey, 0, titleKey);
        }

        // ------------------------------------------------------------------ sectors

        /// <summary>PES_scrambling_control bits of a DVD-Video pack (byte 0x14).</summary>
        public static bool IsScrambled(byte[] sec, int offset)
        {
            return (sec[offset + 0x14] & 0x30) != 0;
        }

        /// <summary>
        /// dvdcss_unscramble: descrambles one 2048-byte sector in place (bytes 0x80..0x7FF) and, like
        /// dvdcss_read, clears the scrambling bits afterwards. Returns false if it was not scrambled.
        /// </summary>
        public static bool Unscramble(byte[] key, byte[] sec, int offset)
        {
            if (!IsScrambled(sec, offset)) return false;

            uint t1 = (uint)(key[0] ^ sec[offset + 0x54]) | 0x100u;
            uint t2 = (uint)(key[1] ^ sec[offset + 0x55]);
            uint t3 = ((uint)key[2] | ((uint)key[3] << 8) | ((uint)key[4] << 16)) ^
                      ((uint)sec[offset + 0x56] | ((uint)sec[offset + 0x57] << 8) | ((uint)sec[offset + 0x58] << 16));
            uint t4 = t3 & 7;
            t3 = t3 * 2 + 8 - t4;
            uint t5 = 0;

            int end = offset + SectorSize;
            for (int p = offset + 0x80; p != end; p++)
            {
                t4 = (uint)(T.CssTab2[t2] ^ T.CssTab3[t1]);
                t2 = t1 >> 1;
                t1 = ((t1 & 1) << 8) ^ t4;
                t4 = T.CssTab5[t4];
                uint t6 = (((((((t3 >> 3) ^ t3) >> 1) ^ t3) >> 8) ^ t3) >> 5) & 0xff;
                t3 = (t3 << 8) | t6;
                t6 = T.CssTab4[t6];
                t5 += t6 + t4;
                sec[p] = (byte)(T.CssTab1[sec[p]] ^ (t5 & 0xff));
                t5 >>= 8;
            }

            sec[offset + 0x14] &= 0x8f;
            return true;
        }

        public static string KeyToString(byte[] key)
        {
            return key[0].ToString("x2") + ":" + key[1].ToString("x2") + ":" + key[2].ToString("x2") + ":" +
                   key[3].ToString("x2") + ":" + key[4].ToString("x2");
        }
    }
}
