// SPDX-License-Identifier: LGPL-2.1-or-later
// AACS drive authentication and key reads, as raw MMC CDBs sent through the app's SCSI pass-through.
// Port of libaacs src/libaacs/mmc.c, Copyright (C) 2009-2010 Obliter0n, (C) 2010-2013 npzacs,
// LGPL-2.1-or-later.
//
//   REPORT KEY (0xA4):          [2..5] address, [6] blocks, [7] key class 2 (AACS), [8..9] length,
//                               [10] AGID << 6 | format
//   SEND KEY (0xA3):            [7] key class 2, [8..9] length, [10] AGID << 6 | format (DATA OUT)
//   READ DISC STRUCTURE (0xAD): [1] media type 1 (BD), [2..5] address, [6] layer, [7] format,
//                               [8..9] length, [10] AGID << 6

using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Bluray.Aacs
{
    internal enum MmcStatus { Success, Error, CertRevoked }

    internal enum MmcRequest { ReadVid, ReadPmsn, ReadDataKeys }

    internal sealed class AacsDrive
    {
        private const byte GetConfiguration = 0x46, SendKey = 0xA3, ReportKey = 0xA4, ReadDiscStructure = 0xAD;
        private const byte KeyClassAacs = 0x02;
        private const byte FmtAgid = 0x00, FmtCertChallenge = 0x01, FmtKey = 0x02, FmtDriveCert = 0x38, FmtInvalidateAgid = 0x3F;
        private const byte BdVolumeId = 0x80, BdPmsn = 0x81, BdDataKeys = 0x84;

        private readonly OpticalDrive _drive;
        private readonly uint _timeout;
        private readonly System.Action<string> _log;

        private readonly byte[] _hostNonce;
        private byte[] _hostKey, _hostKeyPoint;
        private readonly byte[] _driveCert = new byte[92];

        public bool ReadDriveCertSupported { get; private set; }
        public int AacsVersion { get; private set; }
        public bool AacsActive { get; private set; }
        public bool BusEncryptionSupported { get; private set; }
        public string FeatureSummary { get; private set; }

        public AacsDrive(OpticalDrive drive, uint timeoutSeconds, System.Action<string> log)
        {
            _drive = drive;
            _timeout = timeoutSeconds;
            _log = log;
            _hostNonce = Rng.Bytes(20);
            AacsCrypto.CreateHostKeyPair(out _hostKey, out _hostKeyPoint);
        }

        // ------------------------------------------------------------ raw commands

        private async Task<bool> ReportKeyAsync(int agid, uint addr, byte blocks, byte format, byte[] buf)
        {
            var cmd = new byte[12];
            cmd[0] = ReportKey;
            Bytes.PutBE32(cmd, 2, addr);
            cmd[6] = blocks;
            cmd[7] = KeyClassAacs;
            Bytes.PutBE16(cmd, 8, buf.Length);
            cmd[10] = (byte)((agid << 6) | (format & 0x3f));
            var r = await _drive.SendAsync(cmd, buf.Length, buf, 0, _timeout);
            return r.Success;
        }

        private async Task<bool> SendKeyAsync(int agid, byte format, byte[] buf)
        {
            var cmd = new byte[12];
            cmd[0] = SendKey;
            cmd[7] = KeyClassAacs;
            Bytes.PutBE16(cmd, 8, buf.Length);
            cmd[10] = (byte)((agid << 6) | (format & 0x3f));
            var r = await _drive.SendOutAsync(cmd, buf, _timeout);
            return r.Success;
        }

        private async Task<bool> ReadDiscStructureAsync(int agid, byte format, byte layer, uint address, byte[] buf)
        {
            var cmd = new byte[12];
            cmd[0] = ReadDiscStructure;
            cmd[1] = 0x01; // media type: BD
            Bytes.PutBE32(cmd, 2, address);
            cmd[6] = layer;
            cmd[7] = format;
            Bytes.PutBE16(cmd, 8, buf.Length);
            cmd[10] = (byte)(agid << 6);
            var r = await _drive.SendAsync(cmd, buf.Length, buf, 0, _timeout);
            return r.Success;
        }

        // ------------------------------------------------------------ AGID

        private Task<bool> InvalidateAgidAsync(int agid)
        {
            return ReportKeyAsync(agid, 0, 0, FmtInvalidateAgid, new byte[2]);
        }

        private async Task InvalidateAgidsAsync()
        {
            for (int agid = 0; agid < 4; agid++) await InvalidateAgidAsync(agid);
        }

        private async Task<int> ReportAgidAsync()
        {
            var buf = new byte[8];
            if (!await ReportKeyAsync(0, 0, 0, FmtAgid, buf)) return -1;
            return buf[7] >> 6;
        }

        // ------------------------------------------------------------ setup (mmc_open)

        /// <summary>
        /// GET CONFIGURATION, AACS feature (0x010D). Returns false if the drive does not report AACS as
        /// active (libaacs carries on anyway on Windows, and so does this port).
        /// </summary>
        public async Task<bool> CheckAacsAsync()
        {
            var buf = new byte[16];
            var cmd = new byte[12];
            cmd[0] = GetConfiguration;
            cmd[1] = 0x01; // RT: current features
            cmd[2] = 0x01;
            cmd[3] = 0x0D;
            Bytes.PutBE16(cmd, 7, buf.Length);
            var r = await _drive.SendAsync(cmd, buf.Length, buf, 0, _timeout);
            if (!r.Success)
            {
                FeatureSummary = "GET CONFIGURATION failed: " + r.Describe();
                return false;
            }
            int feature = Bytes.BE16(buf, 8);
            if (feature != 0x010D)
            {
                FeatureSummary = "no AACS feature descriptor (got 0x" + feature.ToString("X4") + ")";
                return false;
            }
            ReadDriveCertSupported = (buf[4 + 8] & 0x10) != 0;
            AacsVersion = buf[7 + 8];
            AacsActive = (buf[2 + 8] & 1) != 0;
            BusEncryptionSupported = (buf[4 + 8] & 2) != 0;
            FeatureSummary = "AACS version " + AacsVersion + ", active " + (AacsActive ? 1 : 0) +
                             ", bus encryption support " + (BusEncryptionSupported ? 1 : 0) +
                             ", read drive certificate " + (ReadDriveCertSupported ? 1 : 0) +
                             ", AGID count " + (buf[6 + 8] & 0xf);
            if (ReadDriveCertSupported) await ReadDriveCertAsync();
            return AacsActive;
        }

        /// <summary>mmc_read_drive_cert: the 92-byte drive certificate, or null.</summary>
        public async Task<byte[]> ReadDriveCertAsync()
        {
            if (_driveCert[0] == 0x01) return (byte[])_driveCert.Clone();
            var buf = new byte[96];
            if (!await ReportKeyAsync(0, 0, 0, FmtDriveCert, buf)) return null;
            if (!AacsCrypto.VerifyDriveCert(buf, 4)) return null;
            System.Buffer.BlockCopy(buf, 4, _driveCert, 0, 92);
            return (byte[])_driveCert.Clone();
        }

        /// <summary>The drive certificate if one was read (type 0x01), else null.</summary>
        public byte[] DriveCert { get { return _driveCert[0] == 0x01 ? (byte[])_driveCert.Clone() : null; } }

        // ------------------------------------------------------------ authentication

        private static bool VerifySignature(byte[] cert, byte[] signature, byte[] nonce, byte[] point)
        {
            var data = new byte[60];
            System.Buffer.BlockCopy(nonce, 0, data, 0, 20);
            System.Buffer.BlockCopy(point, 0, data, 20, 40);
            return AacsCrypto.Verify(cert, signature, 0, data, 0, 60);
        }

        private async Task<MmcStatus> AuthAsync(int agid, byte[] hostPrivKey, byte[] hostCert, byte[] busKeyOut)
        {
            // send host cert + nonce
            var buf = new byte[116];
            buf[1] = 0x72;
            System.Buffer.BlockCopy(_hostNonce, 0, buf, 4, 20);
            System.Buffer.BlockCopy(hostCert, 0, buf, 24, 92);
            if (!await SendKeyAsync(agid, FmtCertChallenge, buf))
            {
                string id = Big.Hex(hostCert, 4, 6);
                if ((_driveCert[1] & 0x01) != 0 && (hostCert[1] & 0x01) == 0)
                    _log("AACS: certificate 0x" + id + " can not be used with a bus encryption capable drive.");
                else
                    _log("AACS: host certificate 0x" + id + " refused: revoked by the drive?");
                return MmcStatus.CertRevoked;
            }

            // receive drive cert + nonce
            buf = new byte[116];
            if (!await ReportKeyAsync(agid, 0, 0, FmtCertChallenge, buf))
            {
                _log("AACS: the drive does not give its certificate.");
                return MmcStatus.Error;
            }
            var dn = new byte[20];
            System.Buffer.BlockCopy(buf, 4, dn, 0, 20);
            System.Buffer.BlockCopy(buf, 24, _driveCert, 0, 92);

            if (_driveCert[0] == 0x11)
            {
                _log("AACS: the drive uses an AACS 2.0 certificate (not supported by libaacs either).");
                return MmcStatus.Error;
            }
            if (!AacsCrypto.VerifyDriveCert(_driveCert, 0))
            {
                _log("AACS: drive certificate signature verification failed.");
                return MmcStatus.Error;
            }

            // receive drive key
            buf = new byte[84];
            if (!await ReportKeyAsync(agid, 0, 0, FmtKey, buf))
            {
                _log("AACS: the drive does not give its drive key.");
                return MmcStatus.Error;
            }
            var dkp = new byte[40];
            var dks = new byte[40];
            System.Buffer.BlockCopy(buf, 4, dkp, 0, 40);
            System.Buffer.BlockCopy(buf, 44, dks, 0, 40);
            if (!VerifySignature(_driveCert, dks, _hostNonce, dkp))
            {
                _log("AACS: the drive signature is invalid.");
                return MmcStatus.Error;
            }

            // sign, verify our own signature, send host key point + signature
            byte[] hks = AacsCrypto.Sign(hostPrivKey, dn, _hostKeyPoint);
            if (!VerifySignature(hostCert, hks, dn, _hostKeyPoint))
            {
                _log("AACS: the signature made with the host key does not verify (private key does not match the certificate?).");
                return MmcStatus.Error;
            }
            buf = new byte[84];
            buf[1] = 0x52;
            System.Buffer.BlockCopy(_hostKeyPoint, 0, buf, 4, 40);
            System.Buffer.BlockCopy(hks, 0, buf, 44, 40);
            if (!await SendKeyAsync(agid, FmtKey, buf))
            {
                _log("AACS: error sending the host signature.");
                return MmcStatus.Error;
            }

            if (busKeyOut != null)
            {
                var bk = AacsCrypto.CreateBusKey(_hostKey, dkp);
                System.Buffer.BlockCopy(bk, 0, busKeyOut, 0, 16);
            }
            return MmcStatus.Success;
        }

        private async Task<MmcStatus> ReadMacProtectedAsync(int agid, byte format, byte[] busKey, byte[] value, string what)
        {
            var buf = new byte[36];
            if (!await ReadDiscStructureAsync(agid, format, 0, 0, buf))
            {
                _log("AACS: unable to read the " + what + " from the drive.");
                return MmcStatus.Error;
            }
            System.Buffer.BlockCopy(buf, 4, value, 0, 16);
            var mac = Aes128.Cmac16(value, 0, busKey);
            if (!Bytes.Equal(mac, 0, buf, 20, 16))
            {
                _log("AACS: the " + what + " MAC is incorrect, so this " + what + " is not correct.");
                return MmcStatus.Error;
            }
            return MmcStatus.Success;
        }

        private async Task<MmcStatus> ReadDataKeysAsync(int agid, byte[] busKey, byte[] readDataKey)
        {
            var buf = new byte[36];
            if (!await ReadDiscStructureAsync(agid, BdDataKeys, 0, 0, buf))
            {
                _log("AACS: unable to read the data keys from the drive.");
                return MmcStatus.Error;
            }
            Aes128.Decrypt(busKey, buf, 4, readDataKey, 0);
            return MmcStatus.Success;
        }

        /// <summary>mmc_read_auth: authenticates with one host certificate and reads one value (16 bytes into p1).</summary>
        public async Task<MmcStatus> ReadAuthAsync(byte[] hostPrivKey, byte[] hostCert, MmcRequest request, byte[] p1)
        {
            await InvalidateAgidsAsync();
            int agid = await ReportAgidAsync();
            if (agid < 0)
            {
                _log("AACS: the drive gave no AGID.");
                return MmcStatus.Error;
            }
            var busKey = new byte[16];
            var status = await AuthAsync(agid, hostPrivKey, hostCert, busKey);
            if (status != MmcStatus.Success)
            {
                await InvalidateAgidAsync(agid);
                return status;
            }
            switch (request)
            {
                case MmcRequest.ReadVid: status = await ReadMacProtectedAsync(agid, BdVolumeId, busKey, p1, "Volume ID"); break;
                case MmcRequest.ReadPmsn: status = await ReadMacProtectedAsync(agid, BdPmsn, busKey, p1, "PMSN"); break;
                default: status = await ReadDataKeysAsync(agid, busKey, p1); break;
            }
            await InvalidateAgidAsync(agid);
            return status;
        }
    }
}
