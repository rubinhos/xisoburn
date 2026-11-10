// SPDX-License-Identifier: GPL-2.0-or-later
// Blu-ray decryption while copying: AACS (libaacs port) on every 6144-byte aligned unit of the
// BDMV/STREAM/*.m2ts files, then the BD+ fixups (libbdplus port), so the ISO plays without keys.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Bluray.Aacs;
using xISOBurn.Bluray.Bdplus;
using xISOBurn.Core;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Bluray
{
    internal sealed class BluRaySession
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private const int UnitSectors = 3;
        private const int UnitBytes = AacsSession.AlignedUnit;

        private readonly OpticalDrive _drive;
        private readonly Logger _log;
        private readonly uint _timeout;
        private readonly string _keys;
        private UdfReader _udf;
        private AacsSession _aacs;

        private sealed class StreamFile
        {
            public UdfFile File;
            public uint Clip;
            public long Units;
            public int CpsUnit = -1;
            public List<BdplusPatch> Patches;
        }

        private struct Run
        {
            public long Lba, Sectors, FileSector;
            public StreamFile S;
        }

        private readonly List<StreamFile> _streams = new List<StreamFile>();
        private readonly List<Run> _runs = new List<Run>(); // sorted by Lba

        public bool AacsReady { get; private set; }
        public bool BdplusReady { get; private set; }
        public long UnitsDecrypted, UnitsFailed, PatchesApplied;
        private bool _failureLogged;

        /// <summary>The keys folder: KEYDB.cfg etc. in it, BD+ player files in bdplus/vm0.</summary>
        public static string KeysFolder(string dataFolder) { return Path.Combine(dataFolder, "keys"); }

        public BluRaySession(OpticalDrive drive, Logger log, uint timeoutSeconds, string keysFolder)
        {
            _drive = drive;
            _log = log;
            _timeout = Math.Min(timeoutSeconds, 20u);
            _keys = keysFolder;
        }

        private void Log(string s) { _log.Info(s); }

        private async Task<bool> ReadSectorsAsync(long lba, int count, byte[] buf)
        {
            var r = await _drive.SendAsync(ScsiCommands.Read10((uint)lba, count), count * SectorSize, buf, 0, _timeout);
            return r.Success && r.DataLength == count * SectorSize;
        }

        /// <summary>Lists the UDF file system of a BD (for the file coverage report). Null if it cannot be read.</summary>
        public static async Task<UdfReader> ReadFileSystemAsync(OpticalDrive drive, uint timeout, Logger log)
        {
            var udf = new UdfReader(async (lba, count, buf) =>
            {
                var r = await drive.SendAsync(ScsiCommands.Read10((uint)lba, count), count * SectorSize, buf, 0, timeout);
                return r.Success && r.DataLength == count * SectorSize;
            });
            string err = await udf.OpenAsync();
            if (err != null)
            {
                log.Info("UDF: " + err + ".");
                return null;
            }
            return udf;
        }

        // ------------------------------------------------------------ setup

        public async Task<bool> StartAsync(CancellationToken ct)
        {
            _log.Section("Blu-ray (AACS / BD+)");
            Directory.CreateDirectory(_keys);
            _udf = new UdfReader(ReadSectorsAsync);
            string err = await _udf.OpenAsync();
            if (err != null)
            {
                Log("Blu-ray: the UDF file system could not be read: " + err + ". The copy goes on without decryption.");
                return false;
            }

            foreach (var f in _udf.Files)
            {
                string p = f.Path.ToUpperInvariant();
                if (!p.StartsWith("BDMV/STREAM/") || !p.EndsWith(".M2TS")) continue;
                string name = Path.GetFileNameWithoutExtension(f.Path);
                uint clip;
                if (!uint.TryParse(name, out clip)) continue;
                var s = new StreamFile { File = f, Clip = clip, Units = f.Size / UnitBytes };
                _streams.Add(s);
                foreach (var e in f.Extents)
                {
                    if (e.FileOffset % SectorSize != 0) continue;
                    _runs.Add(new Run { Lba = e.Lba, Sectors = (e.Length + SectorSize - 1) / SectorSize, FileSector = e.FileOffset / SectorSize, S = s });
                }
            }
            _runs.Sort((a, b) => a.Lba.CompareTo(b.Lba));
            long streamBytes = 0;
            foreach (var s in _streams) streamBytes += s.File.Size;
            Log("Blu-ray: " + _udf.Files.Count + " files, " + _streams.Count + " stream file(s) (" + (streamBytes / 1073741824.0).ToString("0.00") + " GiB).");
            if (_udf.Find("AACS/Unit_Key_RO.inf") == null)
            {
                Log("Blu-ray: no AACS directory: the disc is not encrypted.");
                return false;
            }

            AacsDrive drive = new AacsDrive(_drive, _timeout, Log);
            try
            {
                bool active = await drive.CheckAacsAsync();
                Log("AACS drive: " + drive.FeatureSummary + (active ? "" : " (not reported active; trying anyway)") + ".");
            }
            catch (Exception ex) { Log("AACS drive: " + ex.Message); }

            _aacs = new AacsSession(ReadDiscFileAsync, drive, _keys, Log);
            bool ok;
            try { ok = await _aacs.OpenAsync(); }
            catch (Exception ex)
            {
                Log("AACS: error: " + ex.GetType().Name + ": " + ex.Message);
                ok = false;
            }
            ct.ThrowIfCancellationRequested();
            if (!ok)
            {
                Log("AACS: not set up: " + (_aacs.Error ?? "see above") + ". The video is copied encrypted.");
                return false;
            }
            AacsReady = true;
            Log("AACS: ready; the stream files are decrypted while copying.");

            await StartBdplusAsync(ct);
            return true;
        }

        private async Task<byte[]> ReadDiscFileAsync(string path)
        {
            var f = _udf.Find(path);
            if (f == null) return null;
            return await _udf.ReadAllAsync(f, 16 * 1024 * 1024);
        }

        private async Task StartBdplusAsync(CancellationToken ct)
        {
            var svmFile = _udf.Find("BDSVM/00000.svm");
            if (svmFile == null)
            {
                Log("BD+: not used by this disc.");
                return;
            }
            Log("BD+: the disc uses BD+ (BDSVM/00000.svm, " + svmFile.Size.ToString("N0") + " bytes).");
            var svm = await _udf.ReadAllAsync(svmFile, DlxVm.MemorySize + 0x18);
            if (svm == null)
            {
                Log("BD+: BDSVM/00000.svm could not be read.");
                return;
            }
            byte[] mk = await _aacs.GetMediaKeyAsync();
            byte[] vid = await _aacs.GetVolumeIdAsync();
            var clips = new List<uint>();
            foreach (var s in _streams) clips.Add(s.Clip);
            var session = new BdplusSession(Path.Combine(_keys, "bdplus"), Log);
            bool ok;
            try
            {
                ok = await Task.Run(() => session.Run(svm, vid, mk, clips, ReadDiscFileSync, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log("BD+: error: " + ex.GetType().Name + ": " + ex.Message);
                ok = false;
            }
            if (!ok)
            {
                Log("BD+: no conversion table. The copy is decrypted (AACS) but the BD+ fixups are missing: " +
                    "the video of this disc will show errors until a player with BD+ support (or a conversion table) fixes it.");
                return;
            }
            foreach (var s in _streams)
            {
                List<BdplusPatch> p;
                if (session.Patches.TryGetValue(s.Clip, out p)) s.Patches = p;
            }
            BdplusReady = true;
        }

        /// <summary>BD+ file access (blocking; runs on the VM thread, with no copy in progress).</summary>
        private int ReadDiscFileSync(string path, long offset, byte[] buf, int bufOffset, int count)
        {
            var f = _udf.Find(path.TrimStart('/'));
            if (f == null) return -1;
            if (count == 0) return 0;
            return _udf.ReadAsync(f, offset, buf, bufOffset, count).GetAwaiter().GetResult();
        }

        // ------------------------------------------------------------ sector -> aligned unit

        private int FindRun(long lba)
        {
            int lo = 0, hi = _runs.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var r = _runs[mid];
                if (lba < r.Lba) hi = mid - 1;
                else if (lba >= r.Lba + r.Sectors) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        private static long LbaOfFileSector(StreamFile s, long fileSector)
        {
            long off = fileSector * SectorSize;
            foreach (var e in s.File.Extents)
                if (off >= e.FileOffset && off < e.FileOffset + e.Length)
                    return e.Lba + (off - e.FileOffset) / SectorSize;
            return -1;
        }

        private struct UnitRef
        {
            public StreamFile S;
            public long Unit;
            public long[] Lbas; // the 3 sectors
        }

        private bool UnitAt(long lba, out UnitRef u)
        {
            u = default(UnitRef);
            int i = FindRun(lba);
            if (i < 0) return false;
            var r = _runs[i];
            long fileSector = r.FileSector + (lba - r.Lba);
            long unit = fileSector / UnitSectors;
            if (unit >= r.S.Units) return false;
            u.S = r.S;
            u.Unit = unit;
            u.Lbas = new long[UnitSectors];
            for (int k = 0; k < UnitSectors; k++)
            {
                long fs = unit * UnitSectors + k;
                u.Lbas[k] = fs >= r.FileSector && fs < r.FileSector + r.Sectors ? r.Lba + (fs - r.FileSector) : LbaOfFileSector(r.S, fs);
                if (u.Lbas[k] < 0) return false;
            }
            return true;
        }

        public bool Active { get { return AacsReady || BdplusReady; } }

        // ------------------------------------------------------------ decryption

        /// <summary>AACS + BD+ on one unit in buf[off..off+6144). Returns true if the unit is now plain.</summary>
        private bool ProcessUnit(StreamFile s, long unit, byte[] buf, int off)
        {
            if (AacsReady && AacsSession.IsEncrypted(buf, off))
            {
                int cps = s.CpsUnit;
                if (_aacs.DecryptUnit(buf, off, ref cps))
                {
                    s.CpsUnit = cps;
                    UnitsDecrypted++;
                }
                else
                {
                    UnitsFailed++;
                    if (!_failureLogged)
                    {
                        _failureLogged = true;
                        Log("AACS: a unit of " + s.File.Path + " (unit " + unit + ") could not be decrypted with any unit key; " +
                            "such units are copied as read.");
                    }
                    return false;
                }
            }
            if (AacsSession.IsEncrypted(buf, off)) return false;
            if (s.Patches != null) ApplyPatches(s.Patches, unit * UnitBytes, buf, off);
            return true;
        }

        private void ApplyPatches(List<BdplusPatch> patches, long start, byte[] buf, int off)
        {
            long end = start + UnitBytes;
            int lo = 0, hi = patches.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (patches[mid].Offset + 5 <= start) lo = mid + 1; else hi = mid;
            }
            for (int i = lo; i < patches.Count && patches[i].Offset < end; i++)
            {
                var p = patches[i];
                for (int k = 0; k < 5; k++)
                {
                    long pos = p.Offset + k;
                    if (pos >= start && pos < end) buf[off + (int)(pos - start)] = p.Data[k];
                }
                PatchesApplied++;
            }
        }

        /// <summary>
        /// Called by the copy before 'count' sectors read at 'lba' (in buf[0..]) are written to the image.
        /// Units entirely in the buffer are processed in place. A unit cut by the start or end of the read
        /// is completed from the image when its other sectors are already there (map says Ok): the
        /// buffer part is fixed in place and the image part is written back here.
        /// </summary>
        public void ProcessRead(long lba, byte[] buf, int count, SectorStateMap map, System.IO.Stream image)
        {
            if (!Active || _runs.Count == 0) return;
            long end = lba + count;
            var done = new HashSet<long>();
            for (long s = lba; s < end; s++)
            {
                if (FindRun(s) < 0) continue;
                UnitRef u;
                if (!UnitAt(s, out u)) continue;
                if (done.Contains(u.Lbas[0])) continue;
                done.Add(u.Lbas[0]);

                bool inBuffer = true;
                for (int k = 0; k < UnitSectors; k++) if (u.Lbas[k] < lba || u.Lbas[k] >= end) inBuffer = false;
                if (inBuffer && u.Lbas[1] == u.Lbas[0] + 1 && u.Lbas[2] == u.Lbas[0] + 2)
                {
                    ProcessUnit(u.S, u.Unit, buf, (int)(u.Lbas[0] - lba) * SectorSize);
                    continue;
                }

                // Gather the unit from the buffer and the image.
                if (image == null) continue;
                bool complete = true;
                for (int k = 0; k < UnitSectors; k++)
                {
                    long l = u.Lbas[k];
                    if ((l < lba || l >= end) && map.Get(l) != SectorState.Ok) complete = false;
                }
                if (!complete) continue;
                var unit = new byte[UnitBytes];
                for (int k = 0; k < UnitSectors; k++)
                {
                    long l = u.Lbas[k];
                    if (l >= lba && l < end) Buffer.BlockCopy(buf, (int)(l - lba) * SectorSize, unit, k * SectorSize, SectorSize);
                    else ReadImage(image, l, unit, k * SectorSize);
                }
                if (!ProcessUnit(u.S, u.Unit, unit, 0)) continue;
                for (int k = 0; k < UnitSectors; k++)
                {
                    long l = u.Lbas[k];
                    if (l >= lba && l < end) Buffer.BlockCopy(unit, k * SectorSize, buf, (int)(l - lba) * SectorSize, SectorSize);
                    else
                    {
                        image.Seek(l * SectorSize, SeekOrigin.Begin);
                        image.Write(unit, k * SectorSize, SectorSize);
                    }
                }
            }
        }

        private static void ReadImage(System.IO.Stream image, long lba, byte[] dst, int off)
        {
            image.Seek(lba * SectorSize, SeekOrigin.Begin);
            int got = 0;
            while (got < SectorSize)
            {
                int r = image.Read(dst, off + got, SectorSize - got);
                if (r <= 0) break;
                got += r;
            }
        }

        /// <summary>
        /// For downloads that follow the copy: a sector of a stream unit whose other sectors have not been
        /// read yet is still encrypted in the image, and will change once the unit is complete.
        /// </summary>
        public bool IsHeld(long lba, SectorStateMap map)
        {
            if (!Active || FindRun(lba) < 0) return false;
            UnitRef u;
            if (!UnitAt(lba, out u)) return false;
            for (int k = 0; k < UnitSectors; k++)
                if (map.Get(u.Lbas[k]) == SectorState.Untried) return true;
            return false;
        }

        /// <summary>Total sectors of the stream files (for the progress of the image pass).</summary>
        public long StreamSectors
        {
            get
            {
                long n = 0;
                foreach (var s in _streams) n += s.Units * UnitSectors;
                return n;
            }
        }

        /// <summary>
        /// Resumed copy: decrypts and patches, in the image file, every complete unit that is still
        /// encrypted (or not patched yet: the BD+ patches can be applied again safely).
        /// </summary>
        public async Task<long> ImagePassAsync(System.IO.Stream image, SectorStateMap map, Action<long> progress, CancellationToken ct)
        {
            long changed = 0;
            var unit = new byte[UnitBytes];
            var before = new byte[UnitBytes];
            foreach (var s in _streams)
            {
                for (long n = 0; n < s.Units; n++)
                {
                    if ((n & 255) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress(UnitSectors * 256);
                        await Task.Yield();
                    }
                    var lbas = new long[UnitSectors];
                    bool ok = true;
                    for (int k = 0; k < UnitSectors && ok; k++)
                    {
                        lbas[k] = LbaOfFileSector(s, n * UnitSectors + k);
                        ok = lbas[k] >= 0 && map.Get(lbas[k]) == SectorState.Ok;
                    }
                    if (!ok) continue;
                    for (int k = 0; k < UnitSectors; k++) ReadImage(image, lbas[k], unit, k * SectorSize);
                    if (!AacsSession.IsEncrypted(unit, 0) && s.Patches == null) continue;
                    Buffer.BlockCopy(unit, 0, before, 0, UnitBytes);
                    if (!ProcessUnit(s, n, unit, 0)) continue;
                    if (Bytes.Equal(unit, 0, before, 0, UnitBytes)) continue;
                    for (int k = 0; k < UnitSectors; k++)
                    {
                        image.Seek(lbas[k] * SectorSize, SeekOrigin.Begin);
                        image.Write(unit, k * SectorSize, SectorSize);
                    }
                    changed++;
                }
            }
            return changed;
        }
    }
}
