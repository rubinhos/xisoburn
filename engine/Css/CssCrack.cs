// SPDX-License-Identifier: GPL-2.0-or-later
// Port to C# of the CSS key attacks from libdvdcss src/css.c
// (Copyright (C) 1999-2008 VideoLAN, GPL-2.0-or-later; CrackDiscKey and RecoverTitleKey designed by
// Frank A. Stevenson; AttackPattern from Ethan Hawke's DeCSSPlus, modified).
// Functions ported: investigate, CrackDiscKey, RecoverTitleKey and AttackPattern. As in CssCipher.cs,
// the code follows the C original line by line, with the uint8_t truncations written out.
// AttackPadding is not ported: it is disabled (#if 0) in libdvdcss too.

using xISOBurn.Util;
using T = xISOBurn.Css.CssTables;

namespace xISOBurn.Css
{
    internal static class CssCrack
    {
        private const int K1TableSize = 65536;
        private const int K1TableWidth = 10;
        private const int BigTableSize = 16777216;

        /// <summary>Tests if a candidate key produces the given hash (the disc key encrypted with itself).</summary>
        private static bool Investigate(byte[] hash, byte[] ckey)
        {
            var key = new byte[CssCipher.KeySize];
            CssCipher.DecryptKey(0, ckey, hash, 0, key);
            return Bytes.Equal(key, 0, ckey, 0, CssCipher.KeySize);
        }

        /// <summary>
        /// CrackDiscKey: recovers the disc key from its hash (the first 5 bytes of the disc key block,
        /// already XORed with the bus key) without any player key. Replaces discKey with the result.
        /// Uses about 64 MiB for its tables and a few seconds of CPU. Returns false if nothing matched
        /// (libdvdcss returns the last candidate in that case; here the caller is told it failed).
        /// </summary>
        public static bool CrackDiscKey(byte[] discKey)
        {
            var B = new byte[5];    // second stage of the mangle cipher
            var C = new byte[5];    // output stage of the mangle cipher (intermediate key)
            var k = new byte[5];    // mangling cipher key, also output from CSS(C)
            var out1 = new byte[5]; // first five output bytes of LFSR1
            var out2 = new byte[5]; // first five output bytes of LFSR2
            uint tmp, tmp2, tmp3, tmp4, tmp5;

            // Lookup table for the possible k[1] values: a count, then up to 9 values, per (B[0], C[1]).
            var k1Table = new byte[K1TableSize * K1TableWidth];
            tmp = (uint)(discKey[0] ^ T.CssTab1[discKey[1]]);
            for (uint i = 0; i < 256; i++)      // k[1]
            {
                tmp2 = T.CssTab1[tmp ^ i];      // CssTab1[B[1]]
                for (uint j = 0; j < 256; j++)  // B[0]
                {
                    tmp3 = j ^ tmp2 ^ i;        // C[1]
                    int cell = (int)(K1TableWidth * (256 * j + tmp3));
                    tmp4 = k1Table[cell];       // count of entries here
                    tmp4 = (byte)(tmp4 + 1);
                    if (tmp4 < K1TableWidth) k1Table[cell + (int)tmp4] = (byte)i;
                    k1Table[cell] = (byte)tmp4;
                }
            }

            // LFSR2 start state indexed by its output bytes 1, 2 and 5.
            var bigTable = new uint[BigTableSize];
            for (uint i = 0; i < BigTableSize; i++)
            {
                tmp = ((i + i) & 0x1fffff0) | 0x8 | (i & 0x7);
                for (int j = 0; j < 5; j++)
                {
                    tmp2 = (((((((tmp >> 3) ^ tmp) >> 1) ^ tmp) >> 8) ^ tmp) >> 5) & 0xff;
                    tmp = (tmp << 8) | tmp2;
                    out2[j] = T.CssTab4[tmp2];
                }
                bigTable[(out2[0] << 16) | (out2[1] << 8) | out2[4]] = i;
            }

            // Reverse the hash.
            tmp5 = (uint)(discKey[0] ^ T.CssTab1[discKey[1]]);
            for (uint nStepA = 0; nStepA < K1TableSize; nStepA++)
            {
                uint lfsr1a = 0x100 | (nStepA >> 8);
                uint lfsr1b = nStepA & 0xff;

                // Generate the first 5 output bytes of LFSR1.
                for (int i = 0; i < 5; i++)
                {
                    tmp = (uint)(T.CssTab2[lfsr1b] ^ T.CssTab3[lfsr1a]);
                    lfsr1b = lfsr1a >> 1;
                    lfsr1a = ((lfsr1a & 1) << 8) ^ tmp;
                    out1[i] = T.CssTab4[tmp];
                }

                C[0] = (byte)(nStepA >> 8);
                C[1] = (byte)(nStepA & 0xff);
                tmp = (uint)(discKey[3] ^ T.CssTab1[discKey[4]]);
                tmp2 = T.CssTab1[discKey[0]];

                // Search through all possible B[0].
                for (uint nStepB = 0; nStepB < 256; nStepB++)
                {
                    // Reverse parts of the mangling cipher.
                    B[0] = (byte)nStepB;
                    k[0] = (byte)(T.CssTab1[B[0]] ^ C[0]);
                    B[4] = (byte)(B[0] ^ k[0] ^ tmp2);
                    k[4] = (byte)(B[4] ^ tmp);
                    int cell = K1TableWidth * (256 * B[0] + C[1]);
                    uint nPossibleK1 = k1Table[cell];

                    // Try out all possible values for k[1].
                    for (uint nTry = 0; nTry < nPossibleK1; nTry++)
                    {
                        // A cell holds at most 9 values; like the C code, a fuller cell reads on into the
                        // next cells (extra candidates, checked like the others), but never past the table.
                        int at = cell + (int)nTry + 1;
                        if (at >= k1Table.Length) break;
                        k[1] = k1Table[at];
                        B[1] = (byte)(tmp5 ^ k[1]);

                        // Reconstruct the output of LFSR2.
                        tmp3 = 0x100u + k[0] - out1[0];
                        out2[0] = (byte)(tmp3 & 0xff);
                        tmp3 = (tmp3 & 0x100) != 0 ? 0x100u : 0xffu;
                        tmp3 = tmp3 + k[1] - out1[1];
                        out2[1] = (byte)(tmp3 & 0xff);
                        tmp3 = 0x100u + k[4] - out1[4];
                        out2[4] = (byte)(tmp3 & 0xff); // can be 1 off

                        // Test the first possible out2[4], then the second.
                        for (int attempt = 0; attempt < 2; attempt++)
                        {
                            if (attempt == 1) out2[4] = (byte)(out2[4] + 0xff);
                            tmp4 = bigTable[(out2[0] << 16) | (out2[1] << 8) | out2[4]];
                            C[2] = (byte)(tmp4 & 0xff);
                            C[3] = (byte)((tmp4 >> 8) & 0xff);
                            C[4] = (byte)((tmp4 >> 16) & 0xff);
                            B[3] = (byte)(T.CssTab1[B[4]] ^ k[4] ^ C[4]);
                            k[3] = (byte)(discKey[2] ^ T.CssTab1[discKey[3]] ^ B[3]);
                            B[2] = (byte)(T.CssTab1[B[3]] ^ k[3] ^ C[3]);
                            k[2] = (byte)(discKey[1] ^ T.CssTab1[discKey[2]] ^ B[2]);

                            if ((B[1] ^ T.CssTab1[B[2]] ^ k[2]) == C[2] && Investigate(discKey, C))
                            {
                                System.Buffer.BlockCopy(C, 0, discKey, 0, 5);
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// RecoverTitleKey: title key recovery from cipher text and a guessed plain text (10 bytes each)
        /// at sec[crypted] and sec[decrypted], with the sector seed at sec[seed]. Returns -1 on failure.
        /// </summary>
        private static int RecoverTitleKey(uint start, byte[] sec, int crypted, int decrypted, int seed, byte[] key)
        {
            var buffer = new byte[10];
            uint t1, t2, t3, t4, t5, t6;
            int exit = -1;

            for (int i = 0; i < 10; i++)
                buffer[i] = (byte)(T.CssTab1[sec[crypted + i]] ^ sec[decrypted + i]);

            for (uint tryValue = start; tryValue < 0x10000; tryValue++)
            {
                t1 = (tryValue >> 8) | 0x100;
                t2 = tryValue & 0xff;
                t3 = 0;
                t5 = 0;

                // Iterate the cipher 4 times to reconstruct LFSR2.
                int i;
                for (i = 0; i < 4; i++)
                {
                    // Advance LFSR1 normally.
                    t4 = (uint)(T.CssTab2[t2] ^ T.CssTab3[t1]);
                    t2 = t1 >> 1;
                    t1 = ((t1 & 1) << 8) ^ t4;
                    t4 = T.CssTab5[t4];
                    // Deduce t6 and t5.
                    t6 = buffer[i];
                    if (t5 != 0) t6 = (t6 + 0xff) & 0x0ff;
                    if (t6 < t4) t6 += 0x100;
                    t6 -= t4;
                    t5 += t6 + t4;
                    t6 = T.CssTab4[t6];
                    // Feed / advance t3 and t5.
                    t3 = (t3 << 8) | t6;
                    t5 >>= 8;
                }

                uint candidate = t3;

                // Iterate 6 more times to validate the candidate key.
                for (; i < 10; i++)
                {
                    t4 = (uint)(T.CssTab2[t2] ^ T.CssTab3[t1]);
                    t2 = t1 >> 1;
                    t1 = ((t1 & 1) << 8) ^ t4;
                    t4 = T.CssTab5[t4];
                    t6 = (((((((t3 >> 3) ^ t3) >> 1) ^ t3) >> 8) ^ t3) >> 5) & 0xff;
                    t3 = (t3 << 8) | t6;
                    t6 = T.CssTab4[t6];
                    t5 += t6 + t4;
                    if ((t5 & 0xff) != buffer[i]) break;
                    t5 >>= 8;
                }

                if (i == 10)
                {
                    // Four backward steps of t3 to deduce the initial state.
                    t3 = candidate;
                    for (i = 0; i < 4; i++)
                    {
                        t1 = t3 & 0xff;
                        t3 = t3 >> 8;
                        // Brute-force search for the byte shifted in.
                        for (uint j = 0; j < 256; j++)
                        {
                            t3 = (t3 & 0x1ffff) | (j << 17);
                            t6 = (((((((t3 >> 3) ^ t3) >> 1) ^ t3) >> 8) ^ t3) >> 5) & 0xff;
                            if (t6 == t1) break;
                        }
                    }

                    t4 = (t3 >> 1) - 4;
                    for (t5 = 0; t5 < 8; t5++)
                    {
                        if (((t4 + t5) * 2 + 8 - ((t4 + t5) & 7)) == t3)
                        {
                            key[0] = (byte)(tryValue >> 8);
                            key[1] = (byte)(tryValue & 0xFF);
                            key[2] = (byte)((t4 + t5) & 0xFF);
                            key[3] = (byte)(((t4 + t5) >> 8) & 0xFF);
                            key[4] = (byte)(((t4 + t5) >> 16) & 0xFF);
                            exit = (int)tryValue + 1;
                        }
                    }
                }
            }

            if (exit >= 0)
            {
                for (int i = 0; i < 5; i++) key[i] ^= sec[seed + i];
            }
            return exit;
        }

        /// <summary>
        /// AttackPattern (DeCSSPlus, modified): finds a repeating pattern just before the scrambled part
        /// of the sector at sec[offset] (bytes 0x00-0x7F are in the clear) and guesses that the first
        /// scrambled bytes continue it. True if a title key was recovered into key.
        /// </summary>
        public static bool AttackPattern(byte[] sec, int offset, byte[] key)
        {
            uint bestPlen = 0, bestP = 0;

            // For all cycle lengths from 2 to 48.
            for (uint i = 2; i < 0x30; i++)
            {
                // Find the number of bytes that repeat in cycles.
                for (uint j = i + 1; j < 0x80 && sec[offset + 0x7F - (int)(j % i)] == sec[offset + 0x7F - (int)j]; j++)
                {
                    if (j > bestPlen)
                    {
                        bestPlen = j;
                        bestP = i;
                    }
                }
            }

            // At most 10 plain text bytes are needed: at least a few repeated bytes that cycled at least once.
            if (bestPlen > 3 && bestPlen / bestP >= 2)
            {
                for (int i = 0; i < 5; i++) key[i] = 0;
                int res = RecoverTitleKey(0, sec, offset + 0x80,
                                          offset + 0x80 - (int)((bestPlen / bestP) * bestP),
                                          offset + 0x54 /* key seed */, key);
                return res >= 0;
            }
            return false;
        }
    }
}
