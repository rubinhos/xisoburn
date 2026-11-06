using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Css;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// Proof of concept: CSS authentication with the Xbox drive, as libdvdcss does it
    /// (GetBusKey + dvdcss_disckey + dvdcss_titlekey in src/css.c), then reading and descrambling
    /// the start of the first title VOB. Every step is logged with its SCSI result, so a single run
    /// on the console answers which part works:
    ///
    ///   1. READ DVD STRUCTURE copyright + REPORT KEY RPC (regions of the disc and of the drive)
    ///   2. baseline: read the VOB without authentication (expected: refused, as in milestone 2)
    ///   3. bus key: REPORT AGID, SEND CHALLENGE (DATA OUT), REPORT KEY1, REPORT CHALLENGE,
    ///      SEND KEY2 (DATA OUT). SEND KEY was never tried on the Xbox before; it is the critical step.
    ///   4. disc key: READ DVD STRUCTURE format 2, ASF, decryption with the player keys
    ///   5. title key: new bus key, REPORT KEY title key at the VOB start, decryption with the disc key
    ///   6. read the VOB again, descramble, check that the result looks like MPEG-2, and save it as
    ///      css-test-*.vob (downloadable from the web page, playable in VLC)
    ///
    /// Nothing is written to the drive except the authentication handshake, which every DVD player does;
    /// the region setting (SEND KEY RPC) is never touched.
    /// </summary>
    internal sealed class CssTest
    {
        private const int SampleSectors = 2048;   // 4 MiB of the VOB
        private const int SectorsPerRead = 16;

        private readonly OpticalDrive _drive;
        private readonly DiscProbe _probe;
        private readonly Logger _log;
        private readonly string _folder;
        private readonly uint _timeout;
        private readonly CssDrive _css;

        private int _agid;
        private readonly byte[] _busKey = new byte[5];
        private readonly List<string> _summary = new List<string>();

        public CssTest(OpticalDrive drive, DiscProbe probe, Logger log, string folder, uint timeout)
        {
            _drive = drive;
            _probe = probe;
            _log = log;
            _folder = folder;
            _timeout = timeout;
            _css = new CssDrive(drive, Math.Min(timeout, 20u));
        }

        /// <summary>Runs every step and returns a one-line verdict for the status bar.</summary>
        public async Task<string> RunAsync(IProgress<string> progress, CancellationToken token)
        {
            _log.Section("CSS test (proof of concept, libdvdcss port)");

            // ---- 1. regions
            progress.Report("1/6 Regions");
            var cr = new byte[8];
            var r = await _css.ReadCopyrightAsync(0, cr);
            _log.Info("READ DVD STRUCTURE copyright: " + r.Describe() +
                      (r.Success ? " | CPST " + cr[4] + ", disc regions " + RegionList(cr[5]) : ""));
            var rpc = new byte[8];
            r = await _css.ReportRpcAsync(rpc);
            if (r.Success)
            {
                _log.Info("REPORT KEY RPC: " + r.Describe() + " | drive region " + RegionList(rpc[5]) +
                          ", type " + (rpc[4] >> 6) + ", vendor changes left " + ((rpc[4] >> 3) & 7) +
                          ", user changes left " + (rpc[4] & 7) + ", RPC scheme " + rpc[6]);
                if ((cr[5] | rpc[5]) == 0xFF)
                    _log.Info("WARNING: no region in common between the disc and the drive: the drive will refuse the keys.");
            }
            else
            {
                _log.Info("REPORT KEY RPC: " + r.Describe());
            }
            int[] asf = new int[1];
            r = await _css.ReportAsfAsync(asf);
            _log.Info("REPORT KEY ASF (before): " + r.Describe() + (r.Success ? " | ASF=" + asf[0] : ""));
            token.ThrowIfCancellationRequested();

            // ---- target: the first title VOB
            DiscFile vob = PickVob();
            if (vob == null)
            {
                _log.Info("No VOB file in the ISO9660 listing (is this a DVD-Video?). The authentication steps still run.");
            }
            else
            {
                _log.Info("Target: " + vob.Path + " at LBA " + vob.Lba + ", " + vob.Sectors + " sectors");
            }

            // ---- 2. baseline read without authentication
            if (vob != null)
            {
                progress.Report("2/6 Read without authentication");
                var probe = await ReadSampleAsync(vob, 64, null);
                _log.Info("Baseline (no authentication): " + probe.Describe());
            }
            token.ThrowIfCancellationRequested();

            // ---- 3. bus key
            progress.Report("3/6 Authentication (bus key)");
            string failed = await GetBusKeyAsync("disc key");
            if (failed != null)
                return Verdict("Authentication failed at " + failed + ".");
            token.ThrowIfCancellationRequested();

            // ---- 4. disc key
            progress.Report("4/6 Disc key");
            var discBlock = new byte[CssCipher.DiscKeySize];
            r = await _css.ReadDiscKeyAsync(_agid, discBlock);
            _log.Info("READ DVD STRUCTURE disc key: " + r.Describe());
            if (!r.Success)
            {
                await _css.InvalidateAgidAsync(_agid);
                return Verdict("The bus key was accepted, but the drive refused the disc key (region?).");
            }
            r = await _css.ReportAsfAsync(asf);
            _log.Info("REPORT KEY ASF (after disc key): " + r.Describe() + (r.Success ? " | ASF=" + asf[0] : ""));
            if (!r.Success || asf[0] != 1)
            {
                await _css.InvalidateAgidAsync(_agid);
                return Verdict("ASF is not 1 after reading the disc key: region mismatch, or the drive did not accept the authentication.");
            }
            _summary.Add("disc key read");

            for (int i = 0; i < discBlock.Length; i++)
                discBlock[i] ^= _busKey[4 - (i % 5)];
            var discKey = new byte[5];
            int player = await Task.Run(() => CssCipher.DecryptDiscKey(discBlock, discKey));
            if (player >= 0)
            {
                _log.Info("Disc key decrypted with player key #" + player + ": " + CssCipher.KeyToString(discKey));
                _summary.Add("disc key decrypted");
            }
            else
            {
                _log.Info("No player key decrypts the disc key (libdvdcss would crack the title key instead; not in this POC).");
            }
            token.ThrowIfCancellationRequested();

            if (vob == null)
                return Verdict("Authentication works. No VOB to test the title key on.");

            // ---- 5. title key
            progress.Report("5/6 Title key");
            failed = await GetBusKeyAsync("title key");
            if (failed != null)
                return Verdict("Second authentication (title key) failed at " + failed + ".");
            var titleKey = new byte[5];
            var flags = new byte[1];
            r = await _css.ReportTitleKeyAsync(_agid, (uint)vob.Lba, titleKey, flags);
            _log.Info("REPORT KEY title key @" + vob.Lba + ": " + r.Describe() +
                      (r.Success ? " | CPM " + (flags[0] >> 7) + ", CP_SEC " + ((flags[0] >> 6) & 1) + ", CGMS " + ((flags[0] >> 4) & 3) : ""));
            var asfR = await _css.ReportAsfAsync(asf);
            _log.Info("REPORT KEY ASF (after title key): " + asfR.Describe() + (asfR.Success ? " | ASF=" + asf[0] : ""));
            if (!r.Success)
            {
                await _css.InvalidateAgidAsync(_agid);
                return Verdict("The drive refused the title key (region?).");
            }
            for (int i = 0; i < 5; i++)
                titleKey[i] ^= _busKey[4 - (i % 5)];
            if (Bytes.AllZero(titleKey, 0, 5))
            {
                _log.Info("Title key is all zeros: this title is not scrambled.");
            }
            else if (player >= 0)
            {
                CssCipher.DecryptTitleKey(discKey, titleKey);
                _log.Info("Title key decrypted: " + CssCipher.KeyToString(titleKey));
                _summary.Add("title key");
            }
            else
            {
                return Verdict("Title key read, but it cannot be decrypted without the disc key.");
            }
            token.ThrowIfCancellationRequested();

            // ---- 6. read + descramble
            progress.Report("6/6 Reading and descrambling the VOB");
            var sample = await ReadSampleAsync(vob, SampleSectors, titleKey);
            _log.Info("After authentication: " + sample.Describe());
            if (sample.Read == 0)
                return Verdict("Keys obtained, but the drive still refuses the VOB sectors.");

            string file = "css-test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".vob";
            File.WriteAllBytes(Path.Combine(_folder, file), sample.Data(sample.Read));
            _log.Info("Saved " + sample.Read + " descrambled sectors as " + file + " (download it from the web page; it plays in VLC).");

            if (sample.Scrambled == 0)
                return Verdict("Authentication works and the VOB reads, but none of the sampled sectors were scrambled.");
            // Random (scrambled) bytes give ~1 start code per 8600 sectors; MPEG-2 video has at least one
            // per slice, so a correct title key gives far more than one per few sectors.
            bool mpegOk = sample.StartCodesAfter >= Math.Max(8, sample.Scrambled / 8) &&
                          sample.StartCodesAfter > 4 * (sample.StartCodesBefore + 1);
            _summary.Add(mpegOk ? "descrambling OK" : "descrambling doubtful");
            return Verdict(mpegOk
                ? "SUCCESS: the Xbox drive authenticated and the VOB was descrambled (" + file + ")."
                : "Sectors read after authentication, but the descrambled data does not look like MPEG-2 (title key wrong?). See " + file + ".");
        }

        private string Verdict(string text)
        {
            string line = text + (_summary.Count > 0 ? " [" + string.Join(", ", _summary) + "]" : "");
            _log.Info("CSS test result: " + line);
            return line;
        }

        /// <summary>
        /// GetBusKey from libdvdcss, step by step. Returns null on success, otherwise the name of the
        /// failed step (already logged).
        /// </summary>
        private async Task<string> GetBusKeyAsync(string purpose)
        {
            _log.Info("-- bus key (for the " + purpose + ")");
            int[] agid = new int[1];
            var r = await _css.ReportAgidAsync(0, agid);
            _log.Info("REPORT KEY AGID: " + r.Describe() + (r.Success ? " | AGID " + agid[0] : ""));
            // A hung earlier session can hold every AGID: invalidate them in turn and retry, as libdvdcss does.
            for (int i = 0; !r.Success && i < 4; i++)
            {
                var inv = await _css.InvalidateAgidAsync(i);
                _log.Info("REPORT KEY invalidate AGID " + i + ": " + inv.Describe());
                r = await _css.ReportAgidAsync(i, agid);
                _log.Info("REPORT KEY AGID: " + r.Describe() + (r.Success ? " | AGID " + agid[0] : ""));
            }
            if (!r.Success) return "REPORT KEY AGID";
            _agid = agid[0];

            var challenge = new byte[10];
            for (int i = 0; i < 10; i++) challenge[i] = (byte)i;
            var buffer = new byte[10];
            for (int i = 0; i < 10; i++) buffer[9 - i] = challenge[i];

            r = await _css.SendChallengeAsync(_agid, buffer);
            _log.Info("SEND KEY challenge (DATA OUT): " + r.Describe());
            if (!r.Success) { await _css.InvalidateAgidAsync(_agid); return "SEND KEY challenge (the Xbox may block DATA OUT commands)"; }

            var k1 = new byte[5];
            r = await _css.ReportKey1Async(_agid, k1);
            _log.Info("REPORT KEY KEY1: " + r.Describe());
            if (!r.Success) { await _css.InvalidateAgidAsync(_agid); return "REPORT KEY KEY1"; }
            var key1 = new byte[5];
            for (int i = 0; i < 5; i++) key1[i] = k1[4 - i];

            int variant = -1;
            var check = new byte[5];
            for (int i = 0; i < 32; i++)
            {
                CssCipher.CryptKey(0, i, challenge, check);
                if (Bytes.Equal(check, 0, key1, 0, 5)) { variant = i; break; }
            }
            if (variant < 0)
            {
                _log.Info("KEY1 " + CssCipher.KeyToString(key1) + " matches none of the 32 variants: the drive would not authenticate (or the CryptKey port is wrong).");
                await _css.InvalidateAgidAsync(_agid);
                return "KEY1 check";
            }
            _log.Info("Drive authenticated, variant " + variant);

            r = await _css.ReportChallengeAsync(_agid, buffer);
            _log.Info("REPORT KEY challenge: " + r.Describe());
            if (!r.Success) { await _css.InvalidateAgidAsync(_agid); return "REPORT KEY challenge"; }
            for (int i = 0; i < 10; i++) challenge[i] = buffer[9 - i];

            var key2 = new byte[5];
            CssCipher.CryptKey(1, variant, challenge, key2);
            var k2 = new byte[5];
            for (int i = 0; i < 5; i++) k2[4 - i] = key2[i];
            r = await _css.SendKey2Async(_agid, k2);
            _log.Info("SEND KEY KEY2 (DATA OUT): " + r.Describe());
            if (!r.Success) { await _css.InvalidateAgidAsync(_agid); return "SEND KEY KEY2 (the drive rejected the host key, or the Xbox blocks DATA OUT)"; }

            Buffer.BlockCopy(key1, 0, challenge, 0, 5);
            Buffer.BlockCopy(key2, 0, challenge, 5, 5);
            CssCipher.CryptKey(2, variant, challenge, _busKey);
            _log.Info("Authentication established. Bus key " + CssCipher.KeyToString(_busKey));
            if (!_summary.Contains("bus key")) _summary.Add("bus key");
            return null;
        }

        private DiscFile PickVob()
        {
            DiscFile any = null;
            foreach (var f in _probe.Files)
            {
                string p = f.Path.ToUpperInvariant();
                if (!p.EndsWith(".VOB") || f.Size == 0) continue;
                if (p.Contains("VTS_") && p.EndsWith("_1.VOB")) return f; // first title VOB: has the title key
                if (any == null && !p.EndsWith("VIDEO_TS.VOB")) any = f;
            }
            return any;
        }

        private sealed class Sample
        {
            public byte[] Buffer;
            public int Read, Requested, Scrambled, Descrambled;
            public int StartCodesBefore, StartCodesAfter;
            public string FirstError;

            public byte[] Data(int sectors)
            {
                var d = new byte[sectors * CssCipher.SectorSize];
                System.Buffer.BlockCopy(Buffer, 0, d, 0, d.Length);
                return d;
            }

            public string Describe()
            {
                return Read + "/" + Requested + " sectors read" +
                       (FirstError != null ? " (stopped: " + FirstError + ")" : "") +
                       ", scrambled " + Scrambled +
                       (Descrambled > 0 ? ", descrambled " + Descrambled + ", MPEG start codes in payload " +
                                          StartCodesBefore + " before / " + StartCodesAfter + " after" : "");
            }
        }

        /// <summary>
        /// Reads up to 'count' sectors from the start of the VOB. With a title key, scrambled sectors are
        /// descrambled; the MPEG start codes (00 00 01) in the scrambled area count before and after, as
        /// a check: scrambled bytes look random, real MPEG-2 video and audio are full of start codes.
        /// </summary>
        private async Task<Sample> ReadSampleAsync(DiscFile vob, int count, byte[] titleKey)
        {
            int n = (int)Math.Min(count, vob.Sectors);
            var s = new Sample { Buffer = new byte[n * CssCipher.SectorSize], Requested = n };
            while (s.Read < n)
            {
                int chunk = Math.Min(SectorsPerRead, n - s.Read);
                var r = await _drive.SendAsync(ScsiCommands.Read10((uint)(vob.Lba + s.Read), chunk),
                                               chunk * CssCipher.SectorSize, s.Buffer, s.Read * CssCipher.SectorSize, _timeout);
                if (!r.Success || r.DataLength != chunk * CssCipher.SectorSize)
                {
                    s.FirstError = "LBA " + (vob.Lba + s.Read) + ": " + r.Describe();
                    break;
                }
                for (int i = 0; i < chunk; i++)
                {
                    int off = (s.Read + i) * CssCipher.SectorSize;
                    if (!CssCipher.IsScrambled(s.Buffer, off)) continue;
                    s.Scrambled++;
                    if (titleKey == null) continue;
                    s.StartCodesBefore += StartCodes(s.Buffer, off);
                    if (CssCipher.Unscramble(titleKey, s.Buffer, off))
                    {
                        s.Descrambled++;
                        s.StartCodesAfter += StartCodes(s.Buffer, off);
                    }
                }
                s.Read += chunk;
            }
            return s;
        }

        private static int StartCodes(byte[] b, int sectorOffset)
        {
            int c = 0;
            int end = sectorOffset + CssCipher.SectorSize - 2;
            for (int i = sectorOffset + 0x80; i < end; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) c++;
            return c;
        }

        /// <summary>Region mask: a SET bit means "not playable in that region" (bit 0 = region 1).</summary>
        private static string RegionList(byte mask)
        {
            var list = new List<string>();
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) == 0) list.Add((i + 1).ToString());
            return list.Count == 0 ? "none (mask 0x" + mask.ToString("X2") + ")" : string.Join(",", list) + " (mask 0x" + mask.ToString("X2") + ")";
        }
    }
}
