// SPDX-License-Identifier: LGPL-2.1-or-later
// AACS-specific cryptography: port of libaacs src/libaacs/crypto.c (crypto_aacs_*),
// Copyright (C) 2009-2010 Obliter0n, (C) 2010-2013 npzacs, LGPL-2.1-or-later.

using System.Numerics;
using xISOBurn.Util;

namespace xISOBurn.Bluray.Aacs
{
    internal static class AacsCrypto
    {
        /// <summary>IV of the AES-CBC used for aligned units and bus encryption.</summary>
        public static readonly byte[] AacsIv =
        {
            0x0b, 0xa0, 0xf8, 0xdd, 0xfe, 0xa6, 0x1f, 0xb3, 0xd8, 0xdf, 0x9f, 0x56, 0x6a, 0x05, 0x0f, 0x78
        };

        private static readonly byte[] AacsLaPubX = { 0x63, 0xC2, 0x1D, 0xFF, 0xB2, 0xB2, 0x79, 0x8A, 0x13, 0xB5, 0x8D, 0x61, 0x16, 0x6C, 0x4E, 0x4A, 0xAC, 0x8A, 0x07, 0x72 };
        private static readonly byte[] AacsLaPubY = { 0x13, 0x7E, 0xC6, 0x38, 0x81, 0x8F, 0xD9, 0x8F, 0xA4, 0xC3, 0x0B, 0x99, 0x67, 0x28, 0xBF, 0x4B, 0x91, 0x7F, 0x6A, 0x27 };
        private static readonly byte[] AacsCcPubX = { 0x78, 0x4C, 0xF5, 0xC3, 0x63, 0x97, 0xA4, 0x39, 0x04, 0x06, 0xA4, 0x9F, 0x78, 0x00, 0xC7, 0x7D, 0xE9, 0x0C, 0xB3, 0x4C };
        private static readonly byte[] AacsCcPubY = { 0x00, 0x1D, 0xF3, 0x6B, 0x8F, 0x2E, 0xCF, 0x83, 0xCD, 0xEE, 0x43, 0x8F, 0x7F, 0xD1, 0xF4, 0x80, 0x6F, 0xD2, 0x0D, 0xE7 };
        private static readonly byte[] Aacs2CcPubX =
        {
            0xE7, 0x0D, 0x49, 0xD2, 0x6F, 0x45, 0xEA, 0xA7, 0x36, 0x93, 0x9D, 0x72, 0x88, 0x2E, 0xD8, 0xFB,
            0xA1, 0x60, 0x70, 0x26, 0x96, 0x39, 0x49, 0x97, 0x04, 0x96, 0xC9, 0x10, 0xEA, 0x5C, 0x9D, 0xC2
        };
        private static readonly byte[] Aacs2CcPubY =
        {
            0xD1, 0xF5, 0x89, 0x7C, 0xEC, 0xB8, 0x44, 0x01, 0x4E, 0x0F, 0xB0, 0x8C, 0xC7, 0x6E, 0x20, 0xE8,
            0x54, 0x5E, 0xCC, 0x27, 0x1E, 0xE4, 0x6C, 0x4A, 0xEF, 0x81, 0xD9, 0x16, 0x9B, 0xF8, 0x41, 0x72
        };

        /// <summary>crypto_aacs_decrypt: AES-CBC with the AACS IV, in place.</summary>
        public static void Decrypt(byte[] key, byte[] buf, int offset, int length)
        {
            Aes128.CbcDecrypt(key, AacsIv, buf, offset, length);
        }

        private static byte[] Slice(byte[] b, int o, int n)
        {
            var r = new byte[n];
            System.Buffer.BlockCopy(b, o, r, 0, n);
            return r;
        }

        /// <summary>crypto_aacs_verify: signature over data with the public key in an AACS 1 certificate.</summary>
        public static bool Verify(byte[] cert, byte[] signature, int sigOffset, byte[] data, int dataOffset, int length)
        {
            return EcCurve.Aacs1.Verify(cert, 12, cert, 32, signature, sigOffset, Hash.Sha1(data, dataOffset, length));
        }

        public static bool VerifyAacsLa(byte[] signature, int sigOffset, byte[] data, int dataOffset, int length)
        {
            return EcCurve.Aacs1.Verify(AacsLaPubX, 0, AacsLaPubY, 0, signature, sigOffset, Hash.Sha1(data, dataOffset, length));
        }

        /// <summary>crypto_aacs_verify_aacscc: content certificate signature (AACS 1 or AACS 2).</summary>
        public static bool VerifyAacsCc(byte[] signature, int sigOffset, byte[] data, int dataOffset, int length)
        {
            switch (data[dataOffset])
            {
                case 0x00:
                    return EcCurve.Aacs1.Verify(AacsCcPubX, 0, AacsCcPubY, 0, signature, sigOffset, Hash.Sha1(data, dataOffset, length));
                case 0x10:
                    return EcCurve.P256.Verify(Aacs2CcPubX, 0, Aacs2CcPubY, 0, signature, sigOffset, Hash.Sha256(data, dataOffset, length));
                default:
                    return false;
            }
        }

        private static bool VerifyCert(byte[] cert, int offset)
        {
            if (Bytes.BE16(cert, offset + 2) != 0x5c) return false;
            var c = Slice(cert, offset, 92);
            return VerifyAacsLa(c, 52, c, 0, 52);
        }

        /// <summary>Host certificate (92 bytes, type 0x02). AACS 2 certificates are not supported, as in libaacs.</summary>
        public static bool VerifyHostCert(byte[] cert)
        {
            return cert[0] == 0x02 && VerifyCert(cert, 0);
        }

        public static bool VerifyDriveCert(byte[] cert, int offset)
        {
            return cert[offset] == 0x01 && VerifyCert(cert, offset);
        }

        /// <summary>crypto_aacs_sign: signature of SHA-1(nonce || point) with the host private key.</summary>
        public static byte[] Sign(byte[] hostPrivKey, byte[] nonce, byte[] point)
        {
            var block = new byte[60];
            System.Buffer.BlockCopy(nonce, 0, block, 0, 20);
            System.Buffer.BlockCopy(point, 0, block, 20, 40);
            return EcCurve.Aacs1.Sign(Big.FromBytes(hostPrivKey), Hash.Sha1(block));
        }

        /// <summary>crypto_create_host_key_pair: random Hk and Hv = Hk * G (40 bytes: x || y).</summary>
        public static void CreateHostKeyPair(out byte[] hostKey, out byte[] hostKeyPoint)
        {
            BigInteger d;
            do
            {
                hostKey = Rng.Bytes(20);
                d = Big.Mod(Big.FromBytes(hostKey), EcCurve.Aacs1.N);
            } while (d.IsZero);
            hostKey = Big.ToBytes(d, 20);
            var q = EcCurve.Aacs1.Multiply(d, EcCurve.Aacs1.G);
            hostKeyPoint = new byte[40];
            Big.ToBytes(q.X, hostKeyPoint, 0, 20);
            Big.ToBytes(q.Y, hostKeyPoint, 20, 20);
        }

        /// <summary>crypto_create_bus_key: lowest 128 bits of the x coordinate of Hk * drive key point.</summary>
        public static byte[] CreateBusKey(byte[] hostKey, byte[] driveKeyPoint)
        {
            var q = new EcPoint(Big.FromBytes(driveKeyPoint, 0, 20), Big.FromBytes(driveKeyPoint, 20, 20));
            var p = EcCurve.Aacs1.Multiply(Big.FromBytes(hostKey), q);
            var x = Big.ToBytes(p.X, 20);
            return Slice(x, 4, 16);
        }
    }
}
