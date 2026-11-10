// SPDX-License-Identifier: LGPL-2.1-or-later
// AACS key "waterfall" and aligned unit decryption. Port of libaacs src/libaacs/aacs.c,
// Copyright (C) 2009-2010 Obliter0n, (C) 2009-2013 npzacs, LGPL-2.1-or-later.
//
// Order, as in libaacs: disc ID = SHA-1(Unit_Key_RO.inf); VUK from the cache, else computed from the
// media key (cache, MKB + device/processing keys, KEYDB entry) and the Volume ID (cache, drive with a
// host certificate, KEYDB entry), else the VUK of the KEYDB entry; failing that the unit keys of the
// KEYDB entry. Bus encryption, if the disc and the drive use it, needs the read data key from the drive.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using xISOBurn.Util;

namespace xISOBurn.Bluray.Aacs
{
    /// <summary>Reads a whole file of the disc ("AACS/MKB_RO.inf"); null if missing or unreadable.</summary>
    internal delegate Task<byte[]> DiscFileReader(string path);

    internal sealed class AacsSession
    {
        public const int AlignedUnit = 6144;
        private const int SectorLen = 2048;

        private readonly DiscFileReader _file;
        private readonly AacsDrive _drive;   // may be null (no drive access)
        private readonly string _keyFolder;
        private readonly Action<string> _log;
        private KeyCache _cache;

        public byte[] DiscId { get; private set; }
        public KeyDb Config { get; private set; }
        public ContentCert Cert { get; private set; }
        public UnitKeyFile Uk { get; private set; }
        public int MkbVersion { get; private set; }
        public byte[] Mk { get; private set; }
        public byte[] Vid { get; private set; }
        public byte[] Vuk { get; private set; }
        /// <summary>Decrypted CPS unit keys; null if none could be obtained.</summary>
        public List<byte[]> UnitKeys { get; private set; }
        public bool BusEncryptionEnabled { get; private set; }
        public bool BusEncryptionCapable { get; private set; }
        private byte[] _readDataKey;
        private byte[] _driveCertHash;
        private bool _poisoned;
        private bool _configLoaded;

        public string Error { get; private set; }

        public AacsSession(DiscFileReader file, AacsDrive drive, string keyFolder, Action<string> log)
        {
            _file = file;
            _drive = drive;
            _keyFolder = keyFolder;
            _log = log;
        }

        private static bool Empty(byte[] k) { return k == null || Bytes.AllZero(k, 0, k.Length); }

        // ------------------------------------------------------------ open (aacs_open_device)

        /// <summary>Runs the whole key setup. Returns true if every encrypted CPS unit has a key.</summary>
        public async Task<bool> OpenAsync()
        {
            Cert = await ReadContentCertAsync();
            BusEncryptionEnabled = Cert != null && Cert.BusEncryptionEnabled;
            if (Cert != null)
                _log("AACS: content certificate " + Big.Hex(Cert.CcId) + (Cert.Aacs2 ? " (AACS 2)" : " (AACS 1)") +
                     ", bus encryption " + (BusEncryptionEnabled ? "enabled" : "not enabled") + ".");

            if (!await CalcTitleHashAsync())
            {
                Error = "the unit key file (AACS/Unit_Key_RO.inf) could not be read";
                return false;
            }
            _log("AACS: disc ID " + Big.Hex(DiscId) + ", " + Uk.EncryptedKeys.Count + " CPS unit key(s).");
            foreach (var w in Uk.Warnings) _log("AACS: WARNING: " + w + ".");

            _cache = new KeyCache(Path.Combine(_keyFolder, "aacs_cache.txt"));
            EnsureConfig();

            bool ok = await CalcUksAsync();
            if (ok && BusEncryptionEnabled)
            {
                await ReadBusEncryptionCapableAsync();
                if (BusEncryptionCapable && !await ReadReadDataKeyAsync())
                {
                    Error = "the disc and the drive use bus encryption, and the read data key could not be read from the drive";
                    ok = false;
                }
            }
            return ok;
        }

        private KeyDb EnsureConfig()
        {
            if (!_configLoaded)
            {
                _configLoaded = true;
                try
                {
                    Config = KeyDb.Load(_keyFolder, DiscId);
                    foreach (var n in Config.Notes) _log("AACS: " + n);
                    if (Config.Empty) _log("AACS: no key files in the keys folder (KEYDB.cfg). Upload them from the web page.");
                }
                catch (Exception ex)
                {
                    _log("AACS: error reading the key files: " + ex.Message);
                    Config = new KeyDb();
                }
            }
            return Config;
        }

        private async Task<ContentCert> ReadContentCertAsync()
        {
            byte[] data = await _file("AACS/Content000.cer");
            if (data == null || data.Length == 0) data = await _file("AACS/Content001.cer");
            if (data == null || data.Length == 0)
            {
                _log("AACS: failed to read the content certificate.");
                return null;
            }
            string error;
            var cc = ContentCert.Parse(data, out error);
            if (cc == null) _log("AACS: content certificate not usable: " + error + ".");
            return cc;
        }

        private async Task<bool> CalcTitleHashAsync()
        {
            bool aacs2 = Cert != null && Cert.Aacs2;
            byte[] data = await _file("AACS/Unit_Key_RO.inf");
            if (data != null && data.Length > 2048) Uk = UnitKeyFile.Parse(data, aacs2);
            if (Uk == null)
            {
                data = await _file("AACS/DUPLICATE/Unit_Key_RO.inf");
                if (data != null && data.Length > 2048) Uk = UnitKeyFile.Parse(data, aacs2);
            }
            if (Uk == null) return false;
            DiscId = Hash.Sha1(data);
            return true;
        }

        private async Task<Mkb> OpenMkbAsync()
        {
            byte[] data = await _file("AACS/MKB_RO.inf");
            if (data == null || data.Length < 4) data = await _file("AACS/DUPLICATE/MKB_RO.inf");
            if (data == null || data.Length < 4) return null;
            return new Mkb(data);
        }

        // ------------------------------------------------------------ MK, VID, VUK

        private async Task<bool> GetMkAsync()
        {
            if (!Empty(Mk)) return true;
            var cached = _cache.Find("mk", DiscId);
            if (cached != null)
            {
                Mk = cached;
                _log("AACS: using the cached media key.");
                return true;
            }

            var mkb = await OpenMkbAsync();
            if (mkb == null)
            {
                _log("AACS: the MKB (AACS/MKB_RO.inf) could not be read.");
            }
            else
            {
                MkbVersion = (int)mkb.Version;
                _log("AACS: MKB version " + MkbVersion + ".");
                if (Cert != null && Cert.Aacs2)
                {
                    _log("AACS: the disc uses AACS 2: the media key cannot be computed from the MKB.");
                }
                else
                {
                    var mk = MediaKey.Calculate(mkb, EnsureConfig(), _log);
                    if (mk != null)
                    {
                        Mk = mk;
                        _log("AACS: media key computed from the MKB: " + Big.Hex(mk));
                        _cache.Save("mk", DiscId, mk);
                        return true;
                    }
                    _log("AACS: no device or processing key opens this MKB (version " + MkbVersion + ").");
                }
            }

            var ce = EnsureConfig().Disc;
            if (ce != null && !Empty(ce.Mk))
            {
                Mk = ce.Mk;
                _poisoned = true;
                _log("AACS: media key from the KEYDB entry: " + Big.Hex(Mk));
                return true;
            }
            return false;
        }

        /// <summary>Authenticates with each host certificate in turn and reads one value from the drive.</summary>
        private async Task<byte[]> MmcReadAuthAsync(MmcRequest request, string what)
        {
            if (_drive == null) return null;
            var db = EnsureConfig();
            if (db.HostCerts.Count == 0)
            {
                _log("AACS: no host certificate in the key files, so the " + what + " cannot be read from the drive.");
                return null;
            }
            var drvCert = _drive.DriveCert;
            foreach (var hc in db.HostCerts)
            {
                if (!AacsCrypto.VerifyHostCert(hc.Cert))
                {
                    _log("AACS: not using invalid host certificate 0x" + Big.Hex(hc.Cert, 4, 6) + ".");
                    continue;
                }
                if (drvCert != null && (drvCert[1] & 0x01) != 0 && (hc.Cert[1] & 0x01) == 0)
                    _log("AACS: certificate 0x" + Big.Hex(hc.Cert, 4, 6) + " does not support bus encryption.");
                _log("AACS: trying host certificate 0x" + Big.Hex(hc.Cert, 4, 6) + " for the " + what + "...");
                var value = new byte[16];
                MmcStatus st;
                try { st = await _drive.ReadAuthAsync(hc.PrivKey, hc.Cert, request, value); }
                catch (Exception ex)
                {
                    _log("AACS: drive error: " + ex.Message);
                    st = MmcStatus.Error;
                }
                if (st == MmcStatus.Success) return value;
            }
            return null;
        }

        private async Task<bool> GetVidAsync()
        {
            if (!Empty(Vid)) return true;
            var cached = _cache.Find("vid", DiscId);
            if (cached != null)
            {
                Vid = cached;
                _log("AACS: using the cached Volume ID.");
                return true;
            }
            var vid = await MmcReadAuthAsync(MmcRequest.ReadVid, "Volume ID");
            if (vid != null)
            {
                Vid = vid;
                _log("AACS: Volume ID read from the drive: " + Big.Hex(vid));
                _cache.Save("vid", DiscId, vid);
                return true;
            }
            var ce = EnsureConfig().Disc;
            if (ce != null && !Empty(ce.Vid))
            {
                Vid = ce.Vid;
                _poisoned = true;
                _log("AACS: Volume ID from the KEYDB entry: " + Big.Hex(Vid));
                return true;
            }
            _log("AACS: no Volume ID (the drive refused, and the KEYDB entry has none).");
            return false;
        }

        private async Task<byte[]> CalcVukAsync()
        {
            if (!await GetMkAsync()) return null;
            if (!await GetVidAsync()) return null;
            var vuk = Aes128.Decrypt(Mk, Vid);
            for (int a = 0; a < 16; a++) vuk[a] ^= Vid[a];
            _log("AACS: volume unique key: " + Big.Hex(vuk));
            if (!_poisoned) _cache.Save("vuk", DiscId, vuk);
            return vuk;
        }

        private async Task<byte[]> GetVukAsync()
        {
            var cached = _cache.Find("vuk", DiscId);
            if (cached != null)
            {
                _log("AACS: using the cached VUK.");
                return cached;
            }
            var vuk = await CalcVukAsync();
            if (vuk != null) return vuk;
            var ce = EnsureConfig().Disc;
            if (ce != null && !Empty(ce.Vuk))
            {
                _poisoned = true;
                _log("AACS: VUK from the KEYDB entry: " + Big.Hex(ce.Vuk));
                return ce.Vuk;
            }
            return null;
        }

        /// <summary>Media key, for BD+ (aacs_get_mk). Null if unknown.</summary>
        public async Task<byte[]> GetMediaKeyAsync()
        {
            return await GetMkAsync() ? Mk : null;
        }

        /// <summary>Volume ID, for BD+ (aacs_get_vid). Null if unknown.</summary>
        public async Task<byte[]> GetVolumeIdAsync()
        {
            return await GetVidAsync() ? Vid : null;
        }

        // ------------------------------------------------------------ unit keys

        private async Task<int> CciUnencryptedAsync(int cpsUnit)
        {
            string name = "AACS/CPSUnit" + cpsUnit.ToString("D5") + ".cci";
            byte[] data = await _file(name);
            if (data == null || data.Length < 2048) data = await _file("AACS/DUPLICATE/CPSUnit" + cpsUnit.ToString("D5") + ".cci");
            if (data == null || data.Length < 2048) return -1;
            return Cci.IsUnencrypted(data);
        }

        private async Task<bool> CalcUksAsync()
        {
            int numUk = Uk.EncryptedKeys.Count;
            if (numUk < 1)
            {
                UnitKeys = new List<byte[]>();
                _log("AACS: the disc has no CPS unit keys.");
                return true;
            }

            var vuk = await GetVukAsync();
            Vuk = vuk;
            if (vuk == null)
            {
                var ce = EnsureConfig().Disc;
                if (ce != null && ce.UnitKeys != null && ce.UnitKeys.Count > 0)
                {
                    if (ce.UnitKeys.Count != numUk)
                        _log("AACS: incomplete unit keys in the KEYDB entry (the disc has " + numUk + ", found " + ce.UnitKeys.Count + ").");
                    UnitKeys = new List<byte[]>(ce.UnitKeys);
                    for (int i = 0; i < UnitKeys.Count; i++)
                        _log("AACS: unit key " + (i + 1) + " from the KEYDB entry: " + Big.Hex(UnitKeys[i]));
                    return true;
                }
            }

            var keys = new List<byte[]>();
            for (int i = 0; i < numUk; i++)
            {
                if (vuk == null)
                {
                    int plain = await CciUnencryptedAsync(i + 1);
                    if (plain != 1)
                    {
                        Error = "no key for this disc: no VUK, no media key + Volume ID and no unit keys " +
                                "(add the disc to KEYDB.cfg, or keys that open its MKB)";
                        return false;
                    }
                    _log("AACS: WARNING: no VUK, but CPS unit " + (i + 1) + " seems to be unencrypted.");
                    keys.Add(new byte[16]);
                    continue;
                }
                var k = Aes128.Decrypt(vuk, Uk.EncryptedKeys[i]);
                keys.Add(k);
                _log("AACS: unit key " + (i + 1) + ": " + Big.Hex(k));
            }
            UnitKeys = keys;
            return true;
        }

        // ------------------------------------------------------------ bus encryption

        private async Task ReadBusEncryptionCapableAsync()
        {
            BusEncryptionCapable = false;
            if (_drive == null) return;
            byte[] cert = null;
            try { cert = await _drive.ReadDriveCertAsync(); }
            catch (Exception) { }
            if (cert != null)
            {
                _driveCertHash = Hash.Sha1(cert);
                BusEncryptionCapable = (cert[1] & 1) != 0;
                _log("AACS: bus encryption capable flag in the drive certificate: " + (BusEncryptionCapable ? 1 : 0) + ".");
            }
            else
            {
                _driveCertHash = new byte[20];
                _log("AACS: unable to read the drive certificate.");
            }
        }

        private async Task<bool> ReadReadDataKeyAsync()
        {
            string type = "rdk-" + Big.Hex(_driveCertHash);
            var key = await MmcReadAuthAsync(MmcRequest.ReadDataKeys, "read data key");
            if (key != null)
            {
                _readDataKey = key;
                _cache.Save(type, DiscId, key);
                return true;
            }
            var cached = _cache.Find(type, DiscId);
            if (cached != null)
            {
                _log("AACS: using the cached read data key.");
                _readDataKey = cached;
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------ stream decryption

        /// <summary>Checks the TS sync bytes of a decrypted unit and clears the copy permission indicator bits.</summary>
        private static bool VerifyTs(byte[] buf, int off)
        {
            for (int i = 0; i < AlignedUnit; i += 192)
                if (buf[off + i + 4] != 0x47) return false;
            for (int i = 0; i < AlignedUnit; i += 192)
                buf[off + i] &= 0x3F;
            return true;
        }

        private static void DecryptWithKey(byte[] uk, byte[] buf, int off)
        {
            var key = new byte[16];
            Aes128.Encrypt(uk, buf, off, key, 0);
            for (int a = 0; a < 16; a++) key[a] ^= buf[off + a];
            AacsCrypto.Decrypt(key, buf, off + 16, AlignedUnit - 16);
        }

        private void DecryptBus(byte[] buf, int off)
        {
            if (!BusEncryptionEnabled || !BusEncryptionCapable || _readDataKey == null) return;
            for (int i = 0; i < AlignedUnit; i += SectorLen)
                AacsCrypto.Decrypt(_readDataKey, buf, off + i + 16, SectorLen - 16);
        }

        /// <summary>True if the unit is marked encrypted (TP_extra_header copy permission indicator).</summary>
        public static bool IsEncrypted(byte[] buf, int off) { return (buf[off] & 0xC0) != 0; }

        /// <summary>
        /// aacs_decrypt_unit for one 6144-byte aligned unit at buf[off], in place. 'cpsUnit' is the key
        /// that worked last for this file (-1 = unknown) and is updated. Returns true if the unit was
        /// plain or is now decrypted; false if no key gives valid TS packets (the unit is left as read).
        /// </summary>
        public bool DecryptUnit(byte[] buf, int off, ref int cpsUnit)
        {
            if (!IsEncrypted(buf, off)) return true;
            if (UnitKeys == null || UnitKeys.Count == 0) return false;

            var original = new byte[AlignedUnit];
            Buffer.BlockCopy(buf, off, original, 0, AlignedUnit);
            DecryptBus(original, 0);

            var work = new byte[AlignedUnit];
            int first = cpsUnit >= 0 && cpsUnit < UnitKeys.Count ? cpsUnit : 0;
            for (int n = 0; n < UnitKeys.Count; n++)
            {
                int i = (first + n) % UnitKeys.Count;
                Buffer.BlockCopy(original, 0, work, 0, AlignedUnit);
                DecryptWithKey(UnitKeys[i], work, 0);
                if (VerifyTs(work, 0))
                {
                    cpsUnit = i;
                    Buffer.BlockCopy(work, 0, buf, off, AlignedUnit);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Small persistent key cache (the keycache of libaacs): "type discid key" per line.</summary>
    internal sealed class KeyCache
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public KeyCache(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var p = line.Split(' ');
                        if (p.Length == 3) _entries[p[0] + " " + p[1]] = p[2];
                    }
            }
            catch (Exception) { }
        }

        public byte[] Find(string type, byte[] discId)
        {
            string v;
            if (discId == null || !_entries.TryGetValue(type + " " + Big.Hex(discId), out v)) return null;
            try { return Big.HexToBytes(v); }
            catch (FormatException) { return null; }
        }

        public void Save(string type, byte[] discId, byte[] key)
        {
            if (discId == null || Bytes.AllZero(discId, 0, discId.Length)) return;
            _entries[type + " " + Big.Hex(discId)] = Big.Hex(key);
            try
            {
                var lines = new List<string>();
                foreach (var kv in _entries) lines.Add(kv.Key + " " + kv.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllLines(_path, lines);
            }
            catch (Exception) { }
        }
    }
}
