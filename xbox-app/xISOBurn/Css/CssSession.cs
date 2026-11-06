// CSS session with the drive: the authentication sequence of libdvdcss src/css.c (GetBusKey,
// dvdcss_disckey, dvdcss_titlekey), Copyright (C) 1999-2008 VideoLAN, GPL-2.0-or-later.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Core;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Css
{
    /// <summary>Title key for the sectors of one VOB file (LBA range, end exclusive).</summary>
    internal sealed class CssTitleKey
    {
        public string File;
        public long Start, End;
        public byte[] Key;
    }

    /// <summary>
    /// Authenticates with the drive, gets the disc key and one title key per VOB file, and descrambles
    /// sectors as the copy reads them. After authentication the drive also stops refusing the
    /// scrambled sectors, so the reads themselves succeed.
    ///
    /// Only the "key" method of libdvdcss: the disc key is decrypted with the player keys. Discs whose
    /// disc key none of the player keys open would need the title key attack (CrackTitleKey), which
    /// is not ported; their video sectors are then copied scrambled, and the log says so.
    /// </summary>
    internal sealed class CssSession
    {
        private readonly CssDrive _css;
        private readonly Logger _log;
        private readonly List<CssTitleKey> _keys = new List<CssTitleKey>();
        private readonly byte[] _busKey = new byte[5];
        private readonly byte[] _discKey = new byte[5];
        private int _agid;

        /// <summary>Scrambled sectors that were left as they are (no key for them).</summary>
        public long LeftScrambled { get; private set; }
        public long Descrambled { get; private set; }
        public int Reauthentications { get; private set; }
        /// <summary>LBA ranges (VOB files) that have a title key.</summary>
        public IReadOnlyList<CssTitleKey> Keys { get { return _keys; } }

        public CssSession(OpticalDrive drive, Logger log, uint timeoutSeconds)
        {
            _css = new CssDrive(drive, Math.Min(timeoutSeconds, 20u));
            _log = log;
        }

        /// <summary>
        /// Full setup: regions, authentication, disc key, title keys for every VOB in 'files'.
        /// Returns false (with the reason logged) if the drive cannot be authenticated.
        /// </summary>
        public async Task<bool> StartAsync(List<DiscFile> files, CancellationToken ct)
        {
            _log.Section("CSS");
            await LogRegionsAsync();

            if (!await AuthenticateAndReadDiscKeyAsync(true))
                return false;
            ct.ThrowIfCancellationRequested();

            var vobs = new List<DiscFile>();
            foreach (var f in files)
                if (f.Size > 0 && f.Path.ToUpperInvariant().EndsWith(".VOB")) vobs.Add(f);
            if (vobs.Count == 0)
            {
                _log.Info("CSS: no VOB file in the ISO9660 listing; nothing to decrypt.");
                return true;
            }

            byte[] previous = null;
            string previousSet = null;
            foreach (var f in vobs)
            {
                ct.ThrowIfCancellationRequested();
                string set = TitleSet(f.Path);
                byte[] key = await TitleKeyAsync((uint)f.Lba);
                if (key == null && previous != null && set == previousSet)
                {
                    // VTS_xx_2..9.VOB belong to the same title as VTS_xx_1.VOB and share its key.
                    key = previous;
                    _log.Info("CSS: " + f.Path + ": using the key of the previous file of the same title set.");
                }
                if (key != null && !Bytes.AllZero(key, 0, 5))
                {
                    _keys.Add(new CssTitleKey { File = f.Path, Start = f.Lba, End = f.End, Key = key });
                    _log.Info("CSS: " + f.Path + " (LBA " + f.Lba + "-" + (f.End - 1) + "): title key " + CssCipher.KeyToString(key));
                }
                else if (key != null)
                {
                    _log.Info("CSS: " + f.Path + ": no title key (not scrambled).");
                }
                else
                {
                    _log.Info("CSS: " + f.Path + ": no title key could be obtained; its scrambled sectors will be copied as they are.");
                }
                previous = key;
                previousSet = set;
            }
            _log.Info("CSS: " + _keys.Count + " of " + vobs.Count + " VOB file(s) have a title key. The video is decrypted while copying.");
            return true;
        }

        /// <summary>
        /// Authenticates again (the drive may forget the authentication after a reset or a long run of
        /// errors) and reads the disc key, which sets ASF again. The title keys stay the same.
        /// </summary>
        public async Task<bool> ReauthenticateAsync()
        {
            Reauthentications++;
            _log.Info("CSS: the drive refused a read; authenticating again (#" + Reauthentications + ").");
            return await AuthenticateAndReadDiscKeyAsync(false);
        }

        /// <summary>
        /// Descrambles, in place, the scrambled sectors among 'count' sectors read at 'lba' into
        /// buffer[0..]. Sectors that are not scrambled are left alone. Returns how many were descrambled.
        /// </summary>
        public int Descramble(long lba, byte[] buffer, int count)
        {
            int done = 0;
            CssTitleKey k = null;
            for (int i = 0; i < count; i++)
            {
                int off = i * CssCipher.SectorSize;
                if (!CssCipher.IsScrambled(buffer, off)) continue;
                long s = lba + i;
                if (k == null || s < k.Start || s >= k.End) k = Find(s);
                if (k != null && CssCipher.Unscramble(k.Key, buffer, off)) { Descrambled++; done++; }
                else LeftScrambled++;
            }
            return done;
        }

        private CssTitleKey Find(long lba)
        {
            foreach (var k in _keys)
                if (lba >= k.Start && lba < k.End) return k;
            return null;
        }

        private static string TitleSet(string path)
        {
            // VIDEO_TS/VTS_01_3.VOB -> VTS_01 (VIDEO_TS.VOB -> VIDEO_TS)
            string name = path.Substring(path.LastIndexOf('/') + 1).ToUpperInvariant();
            return name.Length >= 6 && name.StartsWith("VTS_") ? name.Substring(0, 6) : name;
        }

        // ------------------------------------------------------------------ steps

        private async Task LogRegionsAsync()
        {
            var cr = new byte[8];
            var r = await _css.ReadCopyrightAsync(0, cr);
            if (r.Success) _log.Info("CSS: disc regions " + RegionList(cr[5]));
            var rpc = new byte[8];
            var r2 = await _css.ReportRpcAsync(rpc);
            if (r2.Success)
            {
                _log.Info("CSS: drive region " + RegionList(rpc[5]) + ", user changes left " + (rpc[4] & 7));
                if (r.Success && (cr[5] | rpc[5]) == 0xFF)
                    _log.Info("CSS: WARNING: the disc and the drive have no region in common; the drive will refuse the keys.");
            }
        }

        private async Task<bool> AuthenticateAndReadDiscKeyAsync(bool decrypt)
        {
            string failed = await GetBusKeyAsync();
            if (failed != null)
            {
                _log.Info("CSS: authentication failed at " + failed + ".");
                return false;
            }

            var block = new byte[CssCipher.DiscKeySize];
            var r = await _css.ReadDiscKeyAsync(_agid, block);
            if (!r.Success)
            {
                _log.Info("CSS: the drive refused the disc key: " + r.Describe());
                await _css.InvalidateAgidAsync(_agid);
                return false;
            }
            int[] asf = new int[1];
            r = await _css.ReportAsfAsync(asf);
            if (!r.Success || asf[0] != 1)
            {
                _log.Info("CSS: ASF is not 1 after the disc key (" + r.Describe() + "): region mismatch?");
                await _css.InvalidateAgidAsync(_agid);
                return false;
            }
            if (!decrypt) return true;

            for (int i = 0; i < block.Length; i++)
                block[i] ^= _busKey[4 - (i % 5)];
            int player = await Task.Run(() => CssCipher.DecryptDiscKey(block, _discKey));
            if (player < 0)
            {
                _log.Info("CSS: none of the player keys decrypts the disc key; the title keys cannot be decrypted " +
                          "(the title key attack of libdvdcss is not ported). The video will stay scrambled.");
                return false;
            }
            _log.Info("CSS: drive authenticated; disc key decrypted with player key #" + player + ".");
            return true;
        }

        private async Task<byte[]> TitleKeyAsync(uint lba)
        {
            // A new session key for every title key, as libdvdcss does.
            string failed = await GetBusKeyAsync();
            if (failed != null)
            {
                _log.Info("CSS: authentication for the title key at LBA " + lba + " failed at " + failed + ".");
                return null;
            }
            var key = new byte[5];
            var flags = new byte[1];
            var r = await _css.ReportTitleKeyAsync(_agid, lba, key, flags);
            if (!r.Success)
            {
                _log.Info("CSS: title key at LBA " + lba + " refused: " + r.Describe());
                await _css.InvalidateAgidAsync(_agid);
                return null;
            }
            for (int i = 0; i < 5; i++)
                key[i] ^= _busKey[4 - (i % 5)];
            if (!Bytes.AllZero(key, 0, 5))
                CssCipher.DecryptTitleKey(_discKey, key);
            return key;
        }

        /// <summary>GetBusKey from libdvdcss. Returns null on success, or the name of the step that failed.</summary>
        private async Task<string> GetBusKeyAsync()
        {
            int[] agid = new int[1];
            var r = await _css.ReportAgidAsync(0, agid);
            // A hung earlier session can hold every AGID: invalidate them in turn and retry.
            for (int i = 0; !r.Success && i < 4; i++)
            {
                await _css.InvalidateAgidAsync(i);
                r = await _css.ReportAgidAsync(i, agid);
            }
            if (!r.Success) return "REPORT KEY AGID (" + r.Describe() + ")";
            _agid = agid[0];

            var challenge = new byte[10];
            for (int i = 0; i < 10; i++) challenge[i] = (byte)i;
            var buffer = new byte[10];
            for (int i = 0; i < 10; i++) buffer[9 - i] = challenge[i];

            r = await _css.SendChallengeAsync(_agid, buffer);
            if (!r.Success) return await FailAsync("SEND KEY challenge", r);

            var k1 = new byte[5];
            r = await _css.ReportKey1Async(_agid, k1);
            if (!r.Success) return await FailAsync("REPORT KEY KEY1", r);
            var key1 = new byte[5];
            for (int i = 0; i < 5; i++) key1[i] = k1[4 - i];

            int variant = -1;
            var check = new byte[5];
            for (int i = 0; i < 32 && variant < 0; i++)
            {
                CssCipher.CryptKey(0, i, challenge, check);
                if (Bytes.Equal(check, 0, key1, 0, 5)) variant = i;
            }
            if (variant < 0)
            {
                await _css.InvalidateAgidAsync(_agid);
                return "KEY1 check (the drive would not authenticate)";
            }

            r = await _css.ReportChallengeAsync(_agid, buffer);
            if (!r.Success) return await FailAsync("REPORT KEY challenge", r);
            for (int i = 0; i < 10; i++) challenge[i] = buffer[9 - i];

            var key2 = new byte[5];
            CssCipher.CryptKey(1, variant, challenge, key2);
            var k2 = new byte[5];
            for (int i = 0; i < 5; i++) k2[4 - i] = key2[i];
            r = await _css.SendKey2Async(_agid, k2);
            if (!r.Success) return await FailAsync("SEND KEY KEY2", r);

            Buffer.BlockCopy(key1, 0, challenge, 0, 5);
            Buffer.BlockCopy(key2, 0, challenge, 5, 5);
            CssCipher.CryptKey(2, variant, challenge, _busKey);
            return null;
        }

        private async Task<string> FailAsync(string step, ScsiResult r)
        {
            await _css.InvalidateAgidAsync(_agid);
            return step + " (" + r.Describe() + ")";
        }

        /// <summary>Region mask: a SET bit means "not playable in that region" (bit 0 = region 1).</summary>
        private static string RegionList(byte mask)
        {
            var list = new List<string>();
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) == 0) list.Add((i + 1).ToString());
            return list.Count == 0 ? "none" : string.Join(",", list);
        }
    }
}
