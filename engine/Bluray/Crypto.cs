// SPDX-License-Identifier: LGPL-2.1-or-later
// Cryptographic primitives used by the libaacs and libbdplus ports, replacing libgcrypt:
// AES-128 (ECB/CBC), AES-CMAC (16-byte input), AES-G3, SHA-1/SHA-256 and ECDSA over prime curves.
// Follows libaacs src/libaacs/crypto.c, Copyright (C) 2009-2010 Obliter0n, (C) 2010-2013 npzacs,
// LGPL-2.1-or-later.

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace xISOBurn.Bluray
{
    /// <summary>AES-128 helpers. Keys are 16 bytes; data lengths are multiples of 16.</summary>
    internal static class Aes128
    {
        [ThreadStatic] private static Aes _aes;

        private static Aes Engine()
        {
            if (_aes == null)
            {
                _aes = Aes.Create();
                _aes.Padding = PaddingMode.None;
            }
            return _aes;
        }

        /// <summary>ECB encryptor for a key, reusable for many blocks (one thread).</summary>
        public static ICryptoTransform EcbEncryptor(byte[] key)
        {
            var aes = Engine();
            aes.Mode = CipherMode.ECB;
            return aes.CreateEncryptor(key, new byte[16]);
        }

        public static ICryptoTransform EcbDecryptor(byte[] key)
        {
            var aes = Engine();
            aes.Mode = CipherMode.ECB;
            return aes.CreateDecryptor(key, new byte[16]);
        }

        /// <summary>crypto_aes128e: one block.</summary>
        public static void Encrypt(byte[] key, byte[] src, int srcOffset, byte[] dst, int dstOffset)
        {
            using (var t = EcbEncryptor(key)) Run(t, src, srcOffset, 16, dst, dstOffset);
        }

        /// <summary>crypto_aes128d: one block.</summary>
        public static void Decrypt(byte[] key, byte[] src, int srcOffset, byte[] dst, int dstOffset)
        {
            using (var t = EcbDecryptor(key)) Run(t, src, srcOffset, 16, dst, dstOffset);
        }

        public static byte[] Encrypt(byte[] key, byte[] block)
        {
            var o = new byte[16];
            Encrypt(key, block, 0, o, 0);
            return o;
        }

        public static byte[] Decrypt(byte[] key, byte[] block)
        {
            var o = new byte[16];
            Decrypt(key, block, 0, o, 0);
            return o;
        }

        /// <summary>CBC decryption in place of buf[offset..offset+length).</summary>
        public static void CbcDecrypt(byte[] key, byte[] iv, byte[] buf, int offset, int length)
        {
            var aes = Engine();
            aes.Mode = CipherMode.CBC;
            using (var t = aes.CreateDecryptor(key, iv))
            {
                byte[] plain = t.TransformFinalBlock(buf, offset, length);
                Buffer.BlockCopy(plain, 0, buf, offset, length);
            }
        }

        /// <summary>Transforms 'length' bytes (whole blocks); safe when src and dst overlap.</summary>
        public static void Run(ICryptoTransform t, byte[] src, int srcOffset, int length, byte[] dst, int dstOffset)
        {
            if (length == 0) return;
            byte[] o = t.TransformFinalBlock(src, srcOffset, length);
            Buffer.BlockCopy(o, 0, dst, dstOffset, length);
        }

        /// <summary>
        /// AES-CMAC of exactly 16 bytes of data (crypto_aes_cmac_16). The input is one complete block,
        /// so only subkey K1 is used.
        /// </summary>
        public static byte[] Cmac16(byte[] data, int dataOffset, byte[] key)
        {
            var zero = new byte[16];
            var l = Encrypt(key, zero);
            var k1 = new byte[16];
            ShiftLeft(l, k1);
            if ((l[0] & 0x80) != 0) k1[15] ^= 0x87;
            var m = new byte[16];
            for (int i = 0; i < 16; i++) m[i] = (byte)(data[dataOffset + i] ^ k1[i]);
            return Encrypt(key, m);
        }

        private static void ShiftLeft(byte[] src, byte[] dst)
        {
            int overflow = 0;
            for (int i = 15; i >= 0; i--)
            {
                dst[i] = (byte)((src[i] << 1) | overflow);
                overflow = src[i] >> 7;
            }
        }

        private static readonly byte[] G3Seed =
        {
            0x7B, 0x10, 0x3C, 0x5D, 0xCB, 0x08, 0xC4, 0xE5, 0x1A, 0x27, 0xB0, 0x17, 0x99, 0x05, 0x3B, 0xD9
        };

        /// <summary>One output of AES-G3: AES-D(key, seed + inc) XOR (seed + inc).</summary>
        private static byte[] G3(byte[] key, int inc)
        {
            var seed = (byte[])G3Seed.Clone();
            seed[15] = (byte)(seed[15] + inc);
            var o = Decrypt(key, seed);
            for (int i = 0; i < 16; i++) o[i] ^= seed[i];
            return o;
        }

        /// <summary>crypto_aesg3: the left child, processing key and right child of a subset-difference node.</summary>
        public static void AesG3(byte[] d, out byte[] leftChild, out byte[] rightChild, out byte[] processingKey)
        {
            leftChild = G3(d, 0);
            processingKey = G3(d, 1);
            rightChild = G3(d, 2);
        }
    }

    internal static class Hash
    {
        public static byte[] Sha1(byte[] data, int offset, int length)
        {
            using (var h = SHA1.Create()) return h.ComputeHash(data, offset, length);
        }

        public static byte[] Sha1(byte[] data) { return Sha1(data, 0, data.Length); }

        public static byte[] Sha256(byte[] data, int offset, int length)
        {
            using (var h = SHA256.Create()) return h.ComputeHash(data, offset, length);
        }
    }

    internal static class Rng
    {
        public static void Fill(byte[] b, int offset, int length)
        {
            var tmp = new byte[length];
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(tmp);
            Buffer.BlockCopy(tmp, 0, b, offset, length);
        }

        public static byte[] Bytes(int n)
        {
            var b = new byte[n];
            Fill(b, 0, n);
            return b;
        }
    }

    /// <summary>Unsigned big-endian byte strings to and from BigInteger.</summary>
    internal static class Big
    {
        public static BigInteger FromBytes(byte[] b, int offset, int length)
        {
            var le = new byte[length + 1];
            for (int i = 0; i < length; i++) le[i] = b[offset + length - 1 - i];
            return new BigInteger(le);
        }

        public static BigInteger FromBytes(byte[] b) { return FromBytes(b, 0, b.Length); }

        public static BigInteger FromHex(string hex)
        {
            return FromBytes(HexToBytes(hex));
        }

        /// <summary>Big-endian, left-padded with zeros to 'length' bytes (higher bytes dropped).</summary>
        public static void ToBytes(BigInteger v, byte[] dst, int offset, int length)
        {
            byte[] le = v.ToByteArray();
            for (int i = 0; i < length; i++)
                dst[offset + length - 1 - i] = i < le.Length ? le[i] : (byte)0;
        }

        public static byte[] ToBytes(BigInteger v, int length)
        {
            var o = new byte[length];
            ToBytes(v, o, 0, length);
            return o;
        }

        public static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 != 0) hex = "0" + hex;
            var o = new byte[hex.Length / 2];
            for (int i = 0; i < o.Length; i++)
                o[i] = (byte)((HexDigit(hex[2 * i]) << 4) | HexDigit(hex[2 * i + 1]));
            return o;
        }

        public static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            throw new FormatException("not a hex digit: " + c);
        }

        public static bool IsHexDigit(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        public static string Hex(byte[] b, int offset, int length)
        {
            var sb = new System.Text.StringBuilder(length * 2);
            for (int i = 0; i < length; i++) sb.Append(b[offset + i].ToString("X2"));
            return sb.ToString();
        }

        public static string Hex(byte[] b) { return b == null ? "" : Hex(b, 0, b.Length); }

        public static BigInteger Mod(BigInteger a, BigInteger m)
        {
            var r = BigInteger.Remainder(a, m);
            return r.Sign < 0 ? r + m : r;
        }

        /// <summary>Inverse modulo a prime.</summary>
        public static BigInteger Inverse(BigInteger a, BigInteger p)
        {
            return BigInteger.ModPow(Mod(a, p), p - 2, p);
        }
    }

    /// <summary>A point in affine coordinates; Infinity is the neutral element.</summary>
    internal sealed class EcPoint
    {
        public readonly BigInteger X, Y;
        public readonly bool Infinity;
        public static readonly EcPoint AtInfinity = new EcPoint();

        private EcPoint() { Infinity = true; }
        public EcPoint(BigInteger x, BigInteger y) { X = x; Y = y; }
    }

    /// <summary>
    /// Short Weierstrass curve y^2 = x^3 + ax + b over GF(p), with ECDSA. Replaces the gcrypt MPI and
    /// S-expression code of libaacs (AACS 1 curve, NIST P-256 for AACS 2 certificates) and libbdplus.
    /// The hash is used as a raw integer, as gcrypt does with "(flags raw)".
    /// </summary>
    internal sealed class EcCurve
    {
        public readonly BigInteger P, A, B, N;
        public readonly EcPoint G;
        public readonly int Size; // bytes per coordinate

        public EcCurve(string p, string a, string b, string n, string gx, string gy, int size)
        {
            P = Big.FromHex(p);
            A = Big.FromHex(a);
            B = Big.FromHex(b);
            N = Big.FromHex(n);
            G = new EcPoint(Big.FromHex(gx), Big.FromHex(gy));
            Size = size;
        }

        /// <summary>AACS 1 curve (libaacs crypto.c).</summary>
        public static readonly EcCurve Aacs1 = new EcCurve(
            "9DC9D81355ECCEB560BDB09EF9EAE7C479A7D7DF",
            "9DC9D81355ECCEB560BDB09EF9EAE7C479A7D7DC",
            "402DAD3EC1CBCD165248D68E1245E0C4DAACB1D8",
            "9DC9D81355ECCEB560BDC44F54817B2C7F5AB017",
            "2E64FC22578351E6F4CCA7EB81D0A4BDC54CCEC6",
            "0914A25DD05442889DB455C7F23C9A0707F5CBB9", 20);

        /// <summary>NIST P-256 (AACS 2 content certificates).</summary>
        public static readonly EcCurve P256 = new EcCurve(
            "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF",
            "FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFC",
            "5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B",
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
            "6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296",
            "4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5", 32);

        /// <summary>BD+ player curve (libbdplus trap.c TRAP_PrivateKey).</summary>
        public static readonly EcCurve Bdplus = new EcCurve(
            "96609D9E935E52C683DBFC3A7D783EA942BDE8CB",
            "96609D9E935E52C683DBFC3A7D783EA942BDE8C8",
            "3E567D8DEC27873BCF86F5FBB595DB288C62C721",
            "96609d9e935e52c683dafdc49216143f9a24373d",
            "05FC5B0B2360AC50A76E1511BC5C9AF67A004D0D",
            "09B0D43F319B09A5B679CCF264E1ABA4D56594EA", 20);

        public bool OnCurve(EcPoint q)
        {
            if (q.Infinity) return true;
            var l = Big.Mod(q.Y * q.Y, P);
            var r = Big.Mod(q.X * q.X * q.X + A * q.X + B, P);
            return l == r;
        }

        public EcPoint Add(EcPoint a, EcPoint b)
        {
            if (a.Infinity) return b;
            if (b.Infinity) return a;
            BigInteger lambda;
            if (a.X == b.X)
            {
                if (Big.Mod(a.Y + b.Y, P).IsZero) return EcPoint.AtInfinity;
                lambda = Big.Mod((3 * a.X * a.X + A) * Big.Inverse(2 * a.Y, P), P);
            }
            else
            {
                lambda = Big.Mod((b.Y - a.Y) * Big.Inverse(b.X - a.X, P), P);
            }
            var x = Big.Mod(lambda * lambda - a.X - b.X, P);
            var y = Big.Mod(lambda * (a.X - x) - a.Y, P);
            return new EcPoint(x, y);
        }

        public EcPoint Multiply(BigInteger k, EcPoint q)
        {
            EcPoint r = EcPoint.AtInfinity;
            EcPoint addend = q;
            k = Big.Mod(k, N);
            while (!k.IsZero)
            {
                if (!k.IsEven) r = Add(r, addend);
                addend = Add(addend, addend);
                k >>= 1;
            }
            return r;
        }

        /// <summary>ECDSA verification; signature = r || s, each Size bytes.</summary>
        public bool Verify(byte[] qx, int qxOffset, byte[] qy, int qyOffset, byte[] signature, int sigOffset, byte[] digest)
        {
            var q = new EcPoint(Big.FromBytes(qx, qxOffset, Size), Big.FromBytes(qy, qyOffset, Size));
            var r = Big.FromBytes(signature, sigOffset, Size);
            var s = Big.FromBytes(signature, sigOffset + Size, Size);
            if (r.Sign <= 0 || r >= N || s.Sign <= 0 || s >= N) return false;
            if (!OnCurve(q)) return false;
            var e = Big.FromBytes(digest);
            var w = Big.Inverse(s, N);
            var u1 = Big.Mod(e * w, N);
            var u2 = Big.Mod(r * w, N);
            var x = Add(Multiply(u1, G), Multiply(u2, q));
            if (x.Infinity) return false;
            return Big.Mod(x.X, N) == r;
        }

        /// <summary>ECDSA signature (r || s, 2 * Size bytes) of a digest with private key d.</summary>
        public byte[] Sign(BigInteger d, byte[] digest)
        {
            var e = Big.FromBytes(digest);
            while (true)
            {
                var k = Big.Mod(Big.FromBytes(Rng.Bytes(Size + 8)), N - 1) + 1;
                var pt = Multiply(k, G);
                var r = Big.Mod(pt.X, N);
                if (r.IsZero) continue;
                var s = Big.Mod(Big.Inverse(k, N) * (e + r * d), N);
                if (s.IsZero) continue;
                var sig = new byte[2 * Size];
                Big.ToBytes(r, sig, 0, Size);
                Big.ToBytes(s, sig, Size, Size);
                return sig;
            }
        }
    }
}
