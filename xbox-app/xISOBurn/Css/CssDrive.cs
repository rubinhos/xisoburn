// CSS commands for the drive, as raw MMC CDBs sent through the existing SCSI pass-through.
// CDB layout and response offsets follow the raw-SCSI branches of libdvdcss src/ioctl.c
// (Solaris USCSI / Haiku), Copyright (C) 1999-2001 VideoLAN, GPL-2.0-or-later.
//
//   REPORT KEY (0xA4), 12 bytes: [2..5] LBA (title key), [7] key class 0 (CSS/CPPM),
//                                [8..9] allocation length, [10] AGID << 6 | key format
//   SEND KEY (0xA3), 12 bytes:   [8..9] parameter list length, [10] AGID << 6 | key format  (DATA OUT)
//   READ DVD STRUCTURE (0xAD):   [6] layer, [7] format, [8..9] allocation length, [10] AGID << 6

using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Css
{
    internal sealed class CssDrive
    {
        private const byte ReportKey = 0xA4;
        private const byte SendKey = 0xA3;
        private const byte ReadDvdStructure = 0xAD;

        // Key formats (MMC-6, table "Key Format code definitions")
        private const byte FmtAgid = 0x00;
        private const byte FmtChallenge = 0x01;
        private const byte FmtKey1 = 0x02;   // REPORT KEY
        private const byte FmtKey2 = 0x03;   // SEND KEY
        private const byte FmtTitleKey = 0x04;
        private const byte FmtAsf = 0x05;
        private const byte FmtRpc = 0x08;
        private const byte FmtInvalidateAgid = 0x3F;

        private readonly OpticalDrive _drive;
        private readonly uint _timeout;

        public CssDrive(OpticalDrive drive, uint timeoutSeconds)
        {
            _drive = drive;
            _timeout = timeoutSeconds;
        }

        private static byte[] Cdb(byte op, int length, int agid, byte format)
        {
            var cdb = new byte[12];
            cdb[0] = op;
            Bytes.PutBE16(cdb, 8, length);
            cdb[10] = (byte)(((agid & 3) << 6) | format);
            return cdb;
        }

        private async Task<ScsiResult> InAsync(byte[] cdb, byte[] buffer)
        {
            return await _drive.SendAsync(cdb, buffer.Length, buffer, 0, _timeout);
        }

        // ------------------------------------------------------------------ REPORT KEY

        /// <summary>REPORT KEY / AGID. On success the AGID is in buffer[7] bits 7-6.</summary>
        public async Task<ScsiResult> ReportAgidAsync(int agid, int[] agidOut)
        {
            var buf = new byte[8];
            var r = await InAsync(Cdb(ReportKey, buf.Length, agid, FmtAgid), buf);
            if (r.Success) agidOut[0] = buf[7] >> 6;
            return r;
        }

        /// <summary>REPORT KEY / Invalidate AGID (format 0x3F, no data).</summary>
        public async Task<ScsiResult> InvalidateAgidAsync(int agid)
        {
            return await _drive.SendAsync(Cdb(ReportKey, 0, agid, FmtInvalidateAgid), 0, null, 0, _timeout);
        }

        /// <summary>REPORT KEY / Challenge: 10 bytes at buffer[4].</summary>
        public async Task<ScsiResult> ReportChallengeAsync(int agid, byte[] challenge)
        {
            var buf = new byte[16];
            var r = await InAsync(Cdb(ReportKey, buf.Length, agid, FmtChallenge), buf);
            if (r.Success) System.Buffer.BlockCopy(buf, 4, challenge, 0, 10);
            return r;
        }

        /// <summary>REPORT KEY / KEY1: 5 bytes at buffer[4].</summary>
        public async Task<ScsiResult> ReportKey1Async(int agid, byte[] key)
        {
            var buf = new byte[12];
            var r = await InAsync(Cdb(ReportKey, buf.Length, agid, FmtKey1), buf);
            if (r.Success) System.Buffer.BlockCopy(buf, 4, key, 0, 5);
            return r;
        }

        /// <summary>REPORT KEY / ASF (authentication success flag): buffer[7] bit 0.</summary>
        public async Task<ScsiResult> ReportAsfAsync(int[] asfOut)
        {
            var buf = new byte[8];
            var r = await InAsync(Cdb(ReportKey, buf.Length, 0, FmtAsf), buf);
            if (r.Success) asfOut[0] = buf[7] & 1;
            return r;
        }

        /// <summary>
        /// REPORT KEY / Title key for the sector 'lba': 5 bytes at buffer[5]; buffer[4] holds the CPM,
        /// CP_SEC and CGMS flags.
        /// </summary>
        public async Task<ScsiResult> ReportTitleKeyAsync(int agid, uint lba, byte[] key, byte[] flagsOut)
        {
            var buf = new byte[12];
            var cdb = Cdb(ReportKey, buf.Length, agid, FmtTitleKey);
            Bytes.PutBE32(cdb, 2, lba);
            var r = await InAsync(cdb, buf);
            if (r.Success)
            {
                System.Buffer.BlockCopy(buf, 5, key, 0, 5);
                flagsOut[0] = buf[4];
            }
            return r;
        }

        /// <summary>REPORT KEY / RPC state: type (bits 7-6 of [4]), region mask [5], RPC scheme [6].</summary>
        public async Task<ScsiResult> ReportRpcAsync(byte[] buf8)
        {
            return await InAsync(Cdb(ReportKey, 8, 0, FmtRpc), buf8);
        }

        // ------------------------------------------------------------------ SEND KEY (DATA OUT)

        /// <summary>SEND KEY / Challenge: 16-byte parameter list, data length 0x000E, challenge at [4].</summary>
        public async Task<ScsiResult> SendChallengeAsync(int agid, byte[] challenge)
        {
            var data = new byte[16];
            data[1] = 0x0e;
            System.Buffer.BlockCopy(challenge, 0, data, 4, 10);
            return await _drive.SendOutAsync(Cdb(SendKey, data.Length, agid, FmtChallenge), data, _timeout);
        }

        /// <summary>SEND KEY / KEY2: 12-byte parameter list, data length 0x000A, key at [4].</summary>
        public async Task<ScsiResult> SendKey2Async(int agid, byte[] key)
        {
            var data = new byte[12];
            data[1] = 0x0a;
            System.Buffer.BlockCopy(key, 0, data, 4, 5);
            return await _drive.SendOutAsync(Cdb(SendKey, data.Length, agid, FmtKey2), data, _timeout);
        }

        // ------------------------------------------------------------------ READ DVD STRUCTURE

        /// <summary>READ DVD STRUCTURE / Disc key (format 0x02): 2048 bytes at buffer[4].</summary>
        public async Task<ScsiResult> ReadDiscKeyAsync(int agid, byte[] discKey)
        {
            var buf = new byte[CssCipher.DiscKeySize + 4];
            var cdb = Cdb(ReadDvdStructure, buf.Length, agid, 0);
            cdb[7] = 0x02;
            var r = await InAsync(cdb, buf);
            if (r.Success) System.Buffer.BlockCopy(buf, 4, discKey, 0, CssCipher.DiscKeySize);
            return r;
        }

        /// <summary>READ DVD STRUCTURE / Copyright (format 0x01): CPST at [4], region mask at [5].</summary>
        public async Task<ScsiResult> ReadCopyrightAsync(byte layer, byte[] buf8)
        {
            var cdb = Cdb(ReadDvdStructure, 8, 0, 0);
            cdb[6] = layer;
            cdb[7] = 0x01;
            return await InAsync(cdb, buf8);
        }
    }
}
