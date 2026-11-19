// SPDX-License-Identifier: LGPL-2.1-or-later
// AACS on-disc structures and the media key calculation. Ports of libaacs:
//   src/libaacs/mkb.c          Copyright (C) 2009-2010 Obliter0n
//   src/libaacs/mk.c           Copyright (C) 2009-2010 Obliter0n, (C) 2009-2026 npzacs
//   src/libaacs/unit_key.c     Copyright (C) 2016 npzacs
//   src/libaacs/content_cert.c Copyright (C) 2015 npzacs
//   src/libaacs/cci.c          Copyright (C) 2016 npzacs
// LGPL-2.1-or-later.

using System;
using System.Collections.Generic;
using xISOBurn.Util;

namespace xISOBurn.Bluray.Aacs
{
    /// <summary>Media Key Block (AACS/MKB_RO.inf): a list of records, type (1 byte) + length (3 bytes).</summary>
    internal sealed class Mkb
    {
        public const uint Type3 = 0x00031003;
        public const uint Type4 = 0x00041003;
        public const uint Type10ClassII = 0x000A1003;
        public const uint Mkb20CategoryC = 0x48141003;
        public const uint Mkb21CategoryC = 0x48151003;

        private readonly byte[] _buf;

        public Mkb(byte[] data) { _buf = data; }

        private static int Be24(byte[] b, int o) { return (b[o] << 16) | (b[o + 1] << 8) | b[o + 2]; }

        /// <summary>Offset of the record of the given type, or -1. 'length' includes the 4-byte header.</summary>
        private int Record(byte type, out int length)
        {
            length = 0;
            int pos = 0;
            while (pos + 4 <= _buf.Length)
            {
                int len = Be24(_buf, pos + 1);
                if (_buf[pos] == type)
                {
                    if (len > _buf.Length - pos) return -1; // truncated
                    length = len;
                    return pos;
                }
                if (len == 0) break;
                pos += len;
            }
            return -1;
        }

        /// <summary>Record body (after its 4-byte header) as offset into Data, or -1.</summary>
        private int Simple(byte type, out int length)
        {
            int rec = Record(type, out length);
            if (length < 4) { length = 0; return -1; }
            if (rec < 0) return -1;
            length -= 4;
            return rec + 4;
        }

        public byte[] Data { get { return _buf; } }

        public uint MkbType
        {
            get
            {
                int len;
                int rec = Record(0x10, out len);
                return rec < 0 || len < 12 ? 0 : Bytes.BE32(_buf, rec + 4);
            }
        }

        public uint Version
        {
            get
            {
                int len;
                int rec = Record(0x10, out len);
                return rec < 0 || len < 12 ? 0 : Bytes.BE32(_buf, rec + 8);
            }
        }

        /// <summary>Subset-difference records: 5 bytes each (u mask shift, uv).</summary>
        public int SubdiffRecords(out int length) { return Simple(0x04, out length); }

        /// <summary>Media key data (C-values), 16 bytes per subset difference.</summary>
        public int CValues(out int length) { return Simple(0x05, out length); }

        /// <summary>Media key verification data (16 bytes), or -1.</summary>
        public int MkDv()
        {
            byte type;
            switch (MkbType)
            {
                case Mkb20CategoryC:
                case Mkb21CategoryC:
                    type = 0x86;
                    break;
                default:
                    type = 0x81;
                    break;
            }
            int len;
            int rec = Simple(type, out len);
            return len < 16 ? -1 : rec;
        }
    }

    /// <summary>Media key calculation from the MKB with device keys or processing keys (mk.c).</summary>
    internal static class MediaKey
    {
        private static readonly byte[] VerifyMagic = { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };

        private static byte[] ValidatePk(byte[] pk, byte[] mkb, int cvalue, int uv, int vd)
        {
            var mk = new byte[16];
            Aes128.Decrypt(pk, mkb, cvalue, mk, 0);
            for (int a = 0; a < 4; a++) mk[a + 12] ^= mkb[uv + a];
            var dec = new byte[16];
            Aes128.Decrypt(mk, mkb, vd, dec, 0);
            return Bytes.Equal(dec, 0, VerifyMagic, 0, 8) ? mk : null;
        }

        private static uint CalcVMask(uint uv)
        {
            uint vMask = 0xffffffff;
            while ((uv & ~vMask) == 0) vMask <<= 1;
            return vMask;
        }

        private static byte[] CalcPk(byte[] dk, uint uv, uint vMask, uint devKeyVMask)
        {
            byte[] left, right, pk;
            Aes128.AesG3(dk, out left, out right, out pk);
            while (devKeyVMask != vMask)
            {
                int i;
                for (i = 31; i >= 0; i--)
                    if ((devKeyVMask & (1u << i)) == 0) break;
                byte[] curr = (i < 0 || (uv & (1u << i)) == 0) ? left : right;
                Aes128.AesG3(curr, out left, out right, out pk);
                devKeyVMask = (uint)((int)devKeyVMask >> 1);
            }
            return pk;
        }

        private static DeviceKey FindDk(List<DeviceKey> dkl, int start, out uint devKeyVMask, uint uv, uint uMask)
        {
            devKeyVMask = 0;
            uint device = dkl[start].Node;
            for (int i = start; i < dkl.Count; i++)
            {
                var dk = dkl[i];
                if (dk.Node != device || dk.Uv == 0) continue;
                uint keyUMask = dk.UMaskShift >= 32 ? 0 : 0xffffffff << dk.UMaskShift;
                uint keyVMask = CalcVMask(dk.Uv);
                if (uMask == keyUMask && (uv & keyVMask) == (dk.Uv & keyVMask))
                {
                    devKeyVMask = keyVMask;
                    return dk;
                }
            }
            return null;
        }

        private static byte[] CalcMkDks(Mkb mkb, List<DeviceKey> dkl, Action<string> log)
        {
            byte[] b = mkb.Data;
            int uvsLen, cLen;
            int uvs = mkb.SubdiffRecords(out uvsLen);
            int cvalues = mkb.CValues(out cLen);
            int dv = mkb.MkDv();
            if (uvs < 0 || cvalues < 0 || dv < 0) return null;
            int numUvs = uvsLen / 5;
            if (numUvs < 1) return null;

            uint device = 0xffffffff;
            for (int n = 0; n < dkl.Count; n++)
            {
                if (dkl[n].Node == device) continue;
                device = dkl[n].Node;

                int idx;
                uint uMask = 0, vMask = 0, uv = 0;
                for (idx = 0; idx < numUvs; idx++)
                {
                    int p = uvs + 1 + 5 * idx;
                    int shift = b[p - 1];
                    uv = Bytes.BE32(b, p);
                    if (uv == 0) continue;
                    if ((shift & 0xc0) != 0)
                    {
                        log("AACS: device 0x" + device.ToString("x") + " is revoked.");
                        idx = numUvs;
                    }
                    else
                    {
                        uMask = shift >= 32 ? 0 : 0xffffffff << shift;
                        vMask = CalcVMask(uv);
                        if ((device & uMask) == (uv & uMask) && (device & vMask) != (uv & vMask)) break;
                    }
                }
                if (idx >= numUvs) continue;

                uint devKeyVMask;
                var dk = FindDk(dkl, n, out devKeyVMask, uv, uMask);
                if (dk == null) continue;
                var pk = CalcPk(dk.Key, uv, vMask, devKeyVMask);
                var mk = ValidatePk(pk, b, cvalues + idx * 16, uvs + 1 + idx * 5, dv);
                if (mk != null) return mk;
            }
            return null;
        }

        private static byte[] CalcMkPks(Mkb mkb, List<byte[]> pkl)
        {
            byte[] b = mkb.Data;
            int dv = mkb.MkDv();
            if (dv < 0) return null;
            int uvsLen, cLen;
            int uvs = mkb.SubdiffRecords(out uvsLen);
            int numUvs = 0;
            if (uvs >= 0)
                for (int rec = uvs; rec < uvs + uvsLen; rec += 5)
                {
                    if ((b[rec] & 0xc0) != 0) break;
                    numUvs++;
                }
            int cvalues = mkb.CValues(out cLen);
            if (cvalues < 0) return null;
            foreach (var pk in pkl)
                for (int a = 0; a < numUvs; a++)
                {
                    if (cvalues + a * 16 + 16 > b.Length || uvs + 1 + a * 5 + 4 > b.Length) break;
                    var mk = ValidatePk(pk, b, cvalues + a * 16, uvs + 1 + a * 5, dv);
                    if (mk != null) return mk;
                }
            return null;
        }

        /// <summary>mk_calculate: device keys first, then processing keys. Null if none applies.</summary>
        public static byte[] Calculate(Mkb mkb, KeyDb db, Action<string> log)
        {
            byte[] mk = null;
            if (db.DeviceKeys.Count > 0) mk = CalcMkDks(mkb, db.DeviceKeys, log);
            if (mk == null) mk = CalcMkPks(mkb, db.ProcessingKeys);
            return mk;
        }
    }

    /// <summary>AACS/Unit_Key_RO.inf (unit_key.c).</summary>
    internal sealed class UnitKeyFile
    {
        public int AppType, NumBdmvDir;
        public bool UseSkbMkb;
        public List<byte[]> EncryptedKeys = new List<byte[]>();
        /// <summary>CPS unit index (0-based) of first play, top menu, then titles 1..N; -1 = invalid.</summary>
        public int[] TitleCpsUnit;
        public readonly List<string> Warnings = new List<string>();

        public static UnitKeyFile Parse(byte[] p, bool aacs2)
        {
            var uk = new UnitKeyFile();
            int size = p.Length;
            if (size < 20) return null;
            uk.AppType = p[16];
            uk.NumBdmvDir = p[17];
            uk.UseSkbMkb = (p[18] >> 7) != 0;
            if (uk.NumBdmvDir < 1) return null;
            if (uk.NumBdmvDir > 1) uk.Warnings.Add("possibly unsupported disc: " + uk.NumBdmvDir + " BDMV directories");
            if (uk.AppType != 1) uk.Warnings.Add("possibly unsupported disc: application type " + uk.AppType);
            if (uk.UseSkbMkb) uk.Warnings.Add("possibly unsupported disc: SKB used");

            // Keys
            if (size < 4) return null;
            uint ukPos = Bytes.BE32(p, 0);
            if ((long)size - 2 < ukPos) return null;
            int numUk = Bytes.BE16(p, (int)ukPos);
            if (numUk >= 1)
            {
                if (size - ukPos < 16) return null;
                if ((size - ukPos - 16) / 48 < numUk) return null;
                if (aacs2 && numUk > 1)
                {
                    if (Bytes.AllZero(p, 48 + 48 + 16, 16)) aacs2 = false;
                    else if (size < ukPos + 64 * uk.EncryptedKeys.Count + 16) return null;
                }
                int pos = (int)ukPos;
                for (int i = 0; i < numUk; i++)
                {
                    pos += 48;
                    if (pos + 16 > size) return null;
                    var k = new byte[16];
                    Buffer.BlockCopy(p, pos, k, 0, 16);
                    if (Bytes.AllZero(k, 0, 16)) uk.Warnings.Add("unit key " + (i + 1) + " is empty");
                    uk.EncryptedKeys.Add(k);
                    if (aacs2) pos += 16;
                }
            }

            // Titles (not fatal)
            if (size >= 26)
            {
                int firstPlay = Bytes.BE16(p, 20), topMenu = Bytes.BE16(p, 22), numTitles = Bytes.BE16(p, 24);
                if (size >= 26 + 4 * numTitles)
                {
                    var map = new int[numTitles + 2];
                    map[0] = firstPlay;
                    map[1] = topMenu;
                    for (int i = 0; i < numTitles; i++) map[i + 2] = Bytes.BE16(p, 26 + 2 + 4 * i);
                    for (int i = 0; i < map.Length; i++)
                        map[i] = (map[i] < 1 || map[i] > uk.EncryptedKeys.Count) ? -1 : map[i] - 1;
                    uk.TitleCpsUnit = map;
                }
            }
            return uk;
        }
    }

    /// <summary>AACS/Content000.cer (content_cert.c).</summary>
    internal sealed class ContentCert
    {
        public bool BusEncryptionEnabled;
        public bool Aacs2;
        public byte[] CcId = new byte[6];

        /// <summary>Null if the certificate is malformed or its signature does not verify.</summary>
        public static ContentCert Parse(byte[] p, out string error)
        {
            error = null;
            if (p.Length < 26) { error = "too short"; return null; }
            int sigSize;
            switch (p[0])
            {
                case 0x00: sigSize = 40; break;
                case 0x10: sigSize = 64; break;
                default: error = "invalid type 0x" + p[0].ToString("X2"); return null;
            }
            int formatSpecific = Bytes.BE16(p, 24);
            int numDigest = Bytes.BE16(p, 12);
            int dataLen = 26 + formatSpecific + numDigest * 8;
            if (p.Length < dataLen + sigSize) { error = "truncated"; return null; }
            if (!AacsCrypto.VerifyAacsCc(p, dataLen, p, 0, dataLen)) { error = "signature verification failed"; return null; }
            var c = new ContentCert();
            c.BusEncryptionEnabled = (p[1] >> 7) != 0;
            Buffer.BlockCopy(p, 14, c.CcId, 0, 6);
            c.Aacs2 = p[0] != 0;
            return c;
        }
    }

    /// <summary>AACS/CPSUnitNNNNN.cci, only what cci_is_unencrypted needs (cci.c).</summary>
    internal static class Cci
    {
        /// <summary>1 = unencrypted, 0 = encrypted, -1 = cannot tell.</summary>
        public static int IsUnencrypted(byte[] data)
        {
            if (data == null || data.Length < 16) return -1;
            int numEntry = Bytes.BE16(data, 0);
            if (numEntry < 1 || numEntry > (2048 - 16) / 6) return -1;
            int p = 16;
            int size = data.Length - 16;
            for (int ii = 0; ii < numEntry; ii++)
            {
                if (size < 6) return -1;
                int type = Bytes.BE16(data, p), version = Bytes.BE16(data, p + 2), length = Bytes.BE16(data, p + 4);
                if (size + 6 < length) return -1;
                if (type == 0x0111) return 0; // enhanced title usage
                if (type == 0x0101)
                {
                    if (length != 0x84) return 0;
                    int epn = (data[p + 6] & 0x04) >> 2;
                    int cci = data[p + 6] & 0x03;
                    int imageConstraint = (data[p + 7] & 0x10) >> 4;
                    int digitalOnly = (data[p + 7] & 0x40) >> 3;
                    int apstb = data[p + 7] & 0x07;
                    int numTitles = Bytes.BE16(data, p + 8);
                    if (numTitles > 1024) return -1;
                    if (version == 0x0100 && cci == 0 && epn == 1 && imageConstraint != 0 && digitalOnly == 0 && apstb == 0)
                    {
                        // _bitcpy of libaacs: whole bytes, then (num_titles & 3) bits of the next byte.
                        var titleType = new byte[128];
                        int bytes = numTitles >> 3, bits = numTitles & 3;
                        Buffer.BlockCopy(data, p + 10, titleType, 0, Math.Min(bytes, data.Length - p - 10));
                        if (bits != 0 && p + 10 + bytes < data.Length)
                            titleType[bytes] = (byte)(data[p + 10 + bytes] & ((((1 << (8 - bits))) - 1) ^ 0xff));
                        for (int jj = 0; jj < (numTitles + 7) / 8; jj++)
                            if (titleType[jj] != 0) return 0;
                        return 1;
                    }
                    return 0;
                }
                p += 6 + length;
                size -= 6 + length;
            }
            return 0;
        }
    }
}
