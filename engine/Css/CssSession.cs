// SPDX-License-Identifier: GPL-2.0-or-later
// CSS session with the drive: the authentication sequence of libdvdcss src/css.c (GetBusKey,
// dvdcss_disckey, dvdcss_titlekey), Copyright (C) 1999-2008 VideoLAN, GPL-2.0-or-later, and the
// CPPM setup of src/libdvdcpxm.c (dvdcpxm_init) and src/cpxm.c (cppm_set_id_album).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Core;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Css
{
    /// <summary>
    /// Key for the sectors of one file (LBA range, end exclusive): a CSS title key for a VOB, or, with
    /// Cppm set, an AOB file decrypted with the session's CPPM album ID and media key (Key is null).
    /// </summary>
    internal sealed class CssTitleKey
    {
        public string File;
        public long Start, End;
        public byte[] Key;
        public bool Cppm;
    }

    /// <summary>
    /// Authenticates with the drive, gets the disc key and one title key per VOB file, and descrambles
    /// sectors as the copy reads them. After authentication the drive also stops refusing the
    /// scrambled sectors, so the reads themselves succeed.
    ///
    /// The methods of libdvdcss, in its order of fallback:
    ///   - "key": the disc key is decrypted with the player keys, then each title key read from the
    ///     drive is decrypted with it;
    ///   - "disc": if no player key opens the disc key, it is cracked from its hash (CrackDiscKey);
    ///   - "title": if there is still no disc key, or the drive refuses a title key, the title key is
    ///     recovered from the scrambled video itself (CrackTitleKey with AttackPattern). This reads the
    ///     start of the VOB, so it needs a drive that serves the scrambled sectors (authenticated, or
    ///     region-free).
    /// </summary>
    internal sealed class CssSession
    {
        private readonly CssDrive _css;
        private readonly Logger _log;
        private readonly List<CssTitleKey> _keys = new List<CssTitleKey>();
        private readonly byte[] _busKey = new byte[5];
        private readonly byte[] _discKey = new byte[5];
        private int _agid;
        private readonly OpticalDrive _drive;
        private readonly uint _timeout;
        /// <summary>The disc key is known (decrypted with a player key, or cracked).</summary>
        private bool _haveDiscKey;
        /// <summary>The disc has VOB files (CSS video), so the disc key is worth cracking.</summary>
        private bool _hasVobs = true;
        /// <summary>The disc key block after the bus key XOR (CPPM reads the album ID from it).</summary>
        private byte[] _discKeyBlock;

        /// <summary>Title keys recovered from the video (method "title").</summary>
        public int CrackedTitleKeys { get; private set; }

        /// <summary>Scrambled sectors that were left as they are (no key for them).</summary>
        public long LeftScrambled { get; private set; }
        public long Descrambled { get; private set; }
        public int Reauthentications { get; private set; }
        /// <summary>LBA ranges (VOB files) that have a title key.</summary>
        public IReadOnlyList<CssTitleKey> Keys { get { return _keys; } }

        public CssSession(OpticalDrive drive, Logger log, uint timeoutSeconds)
        {
            _css = new CssDrive(drive, Math.Min(timeoutSeconds, 20u));
            _drive = drive;
            _timeout = Math.Min(timeoutSeconds, 20u);
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

            var vobs = new List<DiscFile>();
            foreach (var f in files)
                if (f.Size > 0 && f.Path.ToUpperInvariant().EndsWith(".VOB")) vobs.Add(f);
            _hasVobs = vobs.Count > 0;

            bool authenticated = await AuthenticateAndReadDiscKeyAsync(true);
            if (!authenticated && _hasVobs)
                _log.Info("CSS: without authentication, trying to recover the title keys from the video itself " +
                          "(only works if the drive serves the scrambled sectors anyway, e.g. a region-free drive).");
            ct.ThrowIfCancellationRequested();

            if (vobs.Count == 0)
                _log.Info("CSS: no VOB file in the ISO9660 listing; no video to decrypt.");
            else
                await TitleKeysAsync(vobs, authenticated, ct);

            // DVD-Audio: the AOB files of AUDIO_TS are protected with CPPM instead.
            await StartCppmAsync(files, authenticated, ct);

            // Without authentication the session is only worth keeping if the disc gave up some keys.
            return authenticated || _keys.Count > 0;
        }

        private async Task TitleKeysAsync(List<DiscFile> vobs, bool authenticated, CancellationToken ct)
        {
            byte[] previous = null;
            string previousSet = null;
            foreach (var f in vobs)
            {
                ct.ThrowIfCancellationRequested();
                string set = TitleSet(f.Path);
                byte[] key = authenticated && _haveDiscKey ? await TitleKeyAsync((uint)f.Lba) : null;
                if (key == null && previous != null && set == previousSet)
                {
                    // VTS_xx_2..9.VOB belong to the same title as VTS_xx_1.VOB and share its key.
                    key = previous;
                    _log.Info("CSS: " + f.Path + ": using the key of the previous file of the same title set.");
                }
                if (key == null)
                {
                    // A refused title key can cost the authentication (ASF back to 0), and the scrambled
                    // sectors are only served with it: authenticate again first, as libdvdcss does.
                    if (authenticated && _haveDiscKey) await AuthenticateAndReadDiscKeyAsync(false);
                    key = await CrackTitleKeyAsync(f, ct);
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
            _log.Info("CSS: " + _keys.Count + " of " + vobs.Count + " VOB file(s) have a title key" +
                      (CrackedTitleKeys > 0 ? " (" + CrackedTitleKeys + " recovered from the video)" : "") +
                      ". The video is decrypted while copying.");
        }

        // ------------------------------------------------------------------ CPPM (DVD-Audio)

        private ulong _albumId, _mediaKey;
        /// <summary>The CPPM media key and album ID are known: the AOB files are decrypted.</summary>
        public bool CppmReady { get; private set; }
        public long CppmDecrypted { get; private set; }

        /// <summary>
        /// DVD-Audio (dvdcpxm_init for CPPM): the album ID comes from the disc key block, the media key
        /// from AUDIO_TS/DVDAUDIO.MKB and the player device keys. Each AOB file then becomes a range
        /// decrypted with CppmCipher. Nothing happens on discs without AOB files.
        /// </summary>
        private async Task StartCppmAsync(List<DiscFile> files, bool authenticated, CancellationToken ct)
        {
            DiscFile mkbFile = null;
            var aobs = new List<DiscFile>();
            foreach (var f in files)
            {
                string p = f.Path.ToUpperInvariant();
                if (p.EndsWith("/DVDAUDIO.MKB") || p == "DVDAUDIO.MKB") mkbFile = f;
                else if (f.Size > 0 && p.EndsWith(".AOB")) aobs.Add(f);
            }
            if (aobs.Count == 0) return;
            _log.Info("CPPM: DVD-Audio disc, " + aobs.Count + " AOB file(s).");
            if (mkbFile == null || mkbFile.Size <= 0)
            {
                _log.Info("CPPM: AUDIO_TS/DVDAUDIO.MKB not found; the audio stays encrypted.");
                return;
            }
            if (!authenticated || _discKeyBlock == null)
            {
                _log.Info("CPPM: the drive is not authenticated, so the album ID cannot be read; the audio stays encrypted.");
                return;
            }
            ct.ThrowIfCancellationRequested();

            // The media key block, padded with zeros to the size libdvdread passes in.
            var mkb = new byte[CppmCipher.MkbSize];
            int sectors = (int)Math.Min(mkbFile.Sectors, (CppmCipher.MkbSize + CssCipher.SectorSize - 1) / CssCipher.SectorSize);
            var raw = new byte[sectors * CssCipher.SectorSize];
            if (!await _drive.ReadSectorsAsync(mkbFile.Lba, sectors, raw, _timeout))
            {
                _log.Info("CPPM: DVDAUDIO.MKB could not be read; the audio stays encrypted.");
                return;
            }
            Buffer.BlockCopy(raw, 0, mkb, 0, (int)Math.Min(Math.Min(raw.Length, mkbFile.Size), mkb.Length));

            _albumId = CppmCipher.AlbumId(_discKeyBlock);
            ulong found = 0;
            bool ok = await Task.Run(() =>
            {
                ulong k;
                bool r = CppmCipher.ProcessMkb(mkb, mkb.Length, out k);
                found = k;
                return r;
            });
            if (!ok)
            {
                _log.Info("CPPM: none of the device keys opens the media key block (revoked?); the audio stays encrypted.");
                return;
            }
            _mediaKey = found;
            CppmReady = true;
            foreach (var f in aobs)
                _keys.Add(new CssTitleKey { File = f.Path, Start = f.Lba, End = f.End, Cppm = true });
            _log.Info("CPPM: media key found, album ID " + _albumId.ToString("x16") + "; the audio of " + aobs.Count +
                      " AOB file(s) is decrypted while copying.");
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
                if (k != null && k.Cppm)
                {
                    if (CppmCipher.DecryptBlock(buffer, off, _albumId, _mediaKey)) { CppmDecrypted++; Descrambled++; done++; }
                    else LeftScrambled++;
                }
                else if (k != null && CssCipher.Unscramble(k.Key, buffer, off)) { Descrambled++; done++; }
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
            _discKeyBlock = block; // CPPM takes the album ID from it
            int player = await Task.Run(() => CssCipher.DecryptDiscKey(block, _discKey));
            if (player >= 0)
            {
                _haveDiscKey = true;
                _log.Info("CSS: drive authenticated; disc key decrypted with player key #" + player + ".");
                return true;
            }
            if (!_hasVobs)
            {
                _log.Info("CSS: drive authenticated (no VOB files, so the disc key is not needed).");
                return true;
            }

            // Method "disc": crack the disc key from its hash (the first 5 bytes of the block).
            _log.Info("CSS: drive authenticated, but none of the player keys decrypts the disc key; cracking it (this takes a moment)...");
            var hash = new byte[5];
            Buffer.BlockCopy(block, 0, hash, 0, 5);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool cracked = await Task.Run(() => CssCrack.CrackDiscKey(hash));
            if (cracked)
            {
                Buffer.BlockCopy(hash, 0, _discKey, 0, 5);
                _haveDiscKey = true;
                _log.Info("CSS: disc key cracked in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s: " + CssCipher.KeyToString(_discKey) + ".");
            }
            else
            {
                _log.Info("CSS: the disc key could not be cracked; the title keys will be recovered from the video instead.");
            }
            return true;
        }

        // ------------------------------------------------------------------ method "title"

        /// <summary>Sectors read at most per VOB file while looking for a title key (256 MiB).</summary>
        private const int MaxCrackSectors = 131072;
        private const int CrackBlock = 16;

        /// <summary>
        /// CrackTitleKey: reads the VOB from its start and runs AttackPattern on each scrambled sector
        /// until a title key comes out. Returns the key, all zeros if the file is not scrambled, or null.
        /// </summary>
        private async Task<byte[]> CrackTitleKeyAsync(DiscFile f, CancellationToken ct)
        {
            _log.Info("CSS: " + f.Path + ": recovering the title key from the video...");
            var buf = new byte[CrackBlock * CssCipher.SectorSize];
            var key = new byte[5];
            long end = Math.Min(f.End, f.Lba + MaxCrackSectors);
            long pos = f.Lba;
            int reads = 0, encrypted = 0, tries = 0, readErrors = 0;
            bool endOfTitle = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (pos < end && !endOfTitle)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(CrackBlock, end - pos);
                if (!await _drive.ReadSectorsAsync(pos, n, buf, _timeout))
                {
                    // A damaged area, or the drive refuses the scrambled sectors (not authenticated).
                    if (++readErrors >= 8)
                    {
                        _log.Info("CSS: " + f.Path + ": the drive refused " + readErrors + " reads; giving up on this file.");
                        break;
                    }
                    pos += n;
                    continue;
                }
                for (int s = 0; s < n; s++)
                {
                    int off = s * CssCipher.SectorSize;
                    // Stop at a block that is not an MPEG pack (end of the stream).
                    if (buf[off] != 0x00 || buf[off + 1] != 0x00 || buf[off + 2] != 0x01) { endOfTitle = true; break; }
                    // PES_scrambling_control does not exist in a system header or padding / private stream 2.
                    byte stream = buf[off + 0x11];
                    if (CssCipher.IsScrambled(buf, off) && stream != 0xbb && stream != 0xbe && stream != 0xbf)
                    {
                        encrypted++;
                        tries++;
                        if (CssCrack.AttackPattern(buf, off, key))
                        {
                            CrackedTitleKeys++;
                            _log.Info("CSS: " + f.Path + ": title key recovered from LBA " + (pos + s) + " after " +
                                      (reads + s + 1) + " sector(s) (" + sw.Elapsed.TotalSeconds.ToString("0.0") + " s).");
                            return key;
                        }
                    }
                }
                pos += n;
                reads += n;
                if (reads >= 2000 && encrypted == 0) break; // no scrambled sector: the file is in the clear
            }

            if (encrypted == 0 && reads > 0)
                return new byte[5];
            _log.Info("CSS: " + f.Path + ": no title key found in " + reads + " sector(s) (" + encrypted + " scrambled, " +
                      tries + " attempt(s)).");
            return null;
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
