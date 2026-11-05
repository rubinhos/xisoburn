using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>No disc in the drive (02/3A): the run stops; this is not a bad sector.</summary>
    internal sealed class MediumRemovedException : Exception
    {
        public MediumRemovedException(string message) : base(message) { }
    }

    internal sealed class RescueProgress
    {
        public string Phase;
        public long PhaseDone, PhaseTotal;   // sectors handled / to handle in the current phase
        public long CurrentLba;
        public double BytesPerSecond;
        public TimeSpan Elapsed;
        public TimeSpan? Remaining;          // for the current phase
        public long Total, Ok, Untried, Pending, Bad;
    }

    internal sealed class RescueResult
    {
        public bool Finished;   // nothing left to try (no '?' or '*' and all retry passes done)
        public bool Cancelled;
        public string Error;
        public long Ok, Untried, Pending, Bad;
        /// <summary>Sectors tried / recovered by the scrape and retry passes of this run.</summary>
        public long RetriedThisRun, RecoveredThisRun;
        public string Sha256;
        public string ImagePath, MapPath;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Multi-pass, resumable disc rescue in the style of GNU ddrescue, written for what the console
    /// showed (docs/milestone-2.md): on the damaged area of the test DVD one 16-sector ECC block failed
    /// on every revolution (~64 sectors), each failing READ took ~6 s, and retries recovered very little.
    /// Retrying every sector immediately cost ~10 minutes per revolution, so instead:
    ///
    ///   Pass 1  - fast copy: read in SectorsPerRead blocks; a failing block is split into its 16-sector
    ///             ECC blocks, each read ONCE; failures become Pending ('*') and reading moves on. Where
    ///             errors are dense it jumps ahead (growing jumps) and leaves the area Untried ('?'), so
    ///             the easy data is secured first.
    ///   Pass 1b - fill: the areas skipped by pass 1, same rules but without jumping.
    ///   Pass 2  - scrape: every Pending sector once, one sector at a time, optionally at a slower speed.
    ///             Still failing = Bad ('-').
    ///   Pass 3+ - retry: RetryPasses rounds over the Bad sectors, one attempt each per round.
    ///
    /// Everything is persisted: the image only ever receives sectors that read OK, and the map
    /// (VOLUME.map) records the state of every sector; the next run resumes from it. The SHA-256 is
    /// computed from the image file once nothing is left to try (data arrives out of order, so it cannot
    /// be hashed while reading).
    /// </summary>
    internal sealed class RescueJob
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private const int EccBlock = 16;                 // DVD ECC block = 16 sectors (32 KiB)
        private const int CarefulSectors = 2048;         // after an error, read ECC-block-sized pieces for this long
        private const int DenseWindow = 4096;            // "dense errors" = this many failures...
        private const int DenseFailures = 4;             // ...within this many sectors
        private const long FirstSkip = 4096;             // first jump (8 MiB), doubled on every consecutive jump
        private const long MaxSkip = 262144;             // largest jump (512 MiB)
        private static readonly TimeSpan MapSaveInterval = TimeSpan.FromSeconds(30);

        private readonly OpticalDrive _drive;
        private readonly DiscProbe _probe;
        private readonly CopyConfig _cfg;
        private readonly Logger _log;
        private readonly string _folder;

        private SectorStateMap _map;
        private FileStream _image;
        private string _imageName, _imagePath, _mapPath;
        private byte[] _buffer;
        private IProgress<RescueProgress> _progress;
        private CancellationToken _ct;
        private readonly Stopwatch _sw = new Stopwatch();
        private SpeedMeter _speed;
        private TimeSpan _lastReport, _lastMapSave;
        private string _phase = "";
        private long _phaseDone, _phaseTotal;
        private int _lastPercentLogged;
        private byte[] _originalErrorPage; // MODE page 01h as found, to restore

        private readonly bool _extraRetryPass;
        private long _retried, _recovered;

        /// <summary>File names used for a disc (also used by the UI to look for a previous rescue).</summary>
        public static string BaseNameFor(CopyConfig cfg, DiscProbe probe)
        {
            string name = cfg.OutputName;
            if (string.IsNullOrWhiteSpace(name)) name = probe.VolumeLabel;
            if (string.IsNullOrWhiteSpace(name)) name = "disc";
            var sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        /// <param name="extraRetryPass">
        /// "Retry failed" button: after the normal passes, one more retry pass over the bad sectors even
        /// if RetryPasses is already used up. Each press of the button is one more pass.
        /// </param>
        public RescueJob(OpticalDrive drive, DiscProbe probe, CopyConfig cfg, Logger log, string folder, bool extraRetryPass = false)
        {
            _drive = drive;
            _probe = probe;
            _cfg = cfg;
            _log = log;
            _folder = folder;
            _extraRetryPass = extraRetryPass;
        }

        public async Task<RescueResult> RunAsync(IProgress<RescueProgress> progress, CancellationToken ct)
        {
            _progress = progress;
            _ct = ct;
            _buffer = new byte[CopyConfig.MaxSectorsPerRead * SectorSize];
            var result = new RescueResult();
            long total = _probe.TotalSectors;
            long first = Math.Min(_cfg.StartSector, total);
            long end = _cfg.SectorLimit > 0 ? Math.Min(total, first + _cfg.SectorLimit) : total;

            string baseName = BaseName();
            _imageName = baseName + ".iso";
            _imagePath = Path.Combine(_folder, _imageName);
            _mapPath = Path.Combine(_folder, baseName + ".map");
            result.MapPath = _mapPath;
            result.ImagePath = _cfg.WriteImage ? _imagePath : null;

            _log.Section("Rescue");
            _log.Info("Settings: " + _cfg.Describe());
            LoadOrCreateMap(total);
            _log.Info("Work range: LBA " + first + " to " + (end - 1) + (first > 0 || end < total ? " (partial range)" : " (whole disc)"));
            _sw.Start();

            try
            {
                OpenImage(total);
                await LogAndApplyDriveRetriesAsync();

                if (_map.CountIn(SectorState.Untried, first, end) > 0)
                    await FastPassAsync("Pass 1: fast copy", first, end, _cfg.SkipOnErrors);
                if (_map.CountIn(SectorState.Untried, first, end) > 0)
                    await FastPassAsync("Pass 1b: fill skipped areas", first, end, false);

                await RestoreDriveRetriesAsync();

                bool slowed = false;
                try
                {
                    if (_map.CountIn(SectorState.Pending, first, end) > 0)
                    {
                        slowed = await SetSpeedAsync(_cfg.RetryReadSpeedKBps);
                        await SectorPassAsync("Pass 2: scrape", SectorState.Pending, first, end);
                    }
                    while (_map.RetryPassesDone < _cfg.RetryPasses && _map.CountIn(SectorState.Bad, first, end) > 0)
                    {
                        if (!slowed) slowed = await SetSpeedAsync(_cfg.RetryReadSpeedKBps);
                        await SectorPassAsync("Pass " + (3 + _map.RetryPassesDone) + ": retry " + (_map.RetryPassesDone + 1) + "/" + _cfg.RetryPasses,
                                              SectorState.Bad, first, end);
                        _map.RetryPassesDone++;
                        SaveMap(null);
                    }
                    if (_extraRetryPass)
                    {
                        if (_map.CountIn(SectorState.Bad, first, end) > 0)
                        {
                            if (!slowed) slowed = await SetSpeedAsync(_cfg.RetryReadSpeedKBps);
                            await SectorPassAsync("Extra retry pass " + (_map.RetryPassesDone + 1), SectorState.Bad, first, end);
                            _map.RetryPassesDone++;
                            SaveMap(null);
                        }
                        else
                        {
                            _log.Info("Retry failed: no bad sectors left to retry in the work range.");
                        }
                    }
                }
                finally
                {
                    if (slowed) await SetSpeedAsync(0xFFFF);
                }

                result.Finished = _map.Count(SectorState.Untried) == 0 && _map.Count(SectorState.Pending) == 0 &&
                                  (_map.RetryPassesDone >= _cfg.RetryPasses || _map.Count(SectorState.Bad) == 0);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                _log.Info("Cancelled.");
            }
            catch (MediumRemovedException ex)
            {
                result.Error = ex.Message;
                _log.Info("STOPPED: " + ex.Message);
            }
            catch (Exception ex)
            {
                result.Error = ex.GetType().Name + ": " + ex.Message + " (0x" + ex.HResult.ToString("X8") + ")";
                _log.Info("ERROR: " + result.Error);
            }
            finally
            {
                try { await RestoreDriveRetriesAsync(); } catch (Exception) { }
                CloseImage();
                SaveMap(null);
            }

            // The hash needs the whole image and nothing left to read.
            if (result.Finished && _cfg.WriteImage && !result.Cancelled && result.Error == null)
            {
                try
                {
                    result.Sha256 = await HashImageAsync(total);
                    File.WriteAllText(Path.Combine(_folder, baseName + ".sha256"), result.Sha256 + " *" + _imageName + "\n", new UTF8Encoding(false));
                    SaveMap(result.Sha256);
                }
                catch (OperationCanceledException)
                {
                    _log.Info("Hash cancelled; it will be computed at the end of the next run.");
                }
                catch (Exception ex)
                {
                    _log.Info("Error computing the hash: " + ex.Message);
                }
            }

            _sw.Stop();
            result.Elapsed = _sw.Elapsed;
            result.Ok = _map.Count(SectorState.Ok);
            result.Untried = _map.Count(SectorState.Untried);
            result.Pending = _map.Count(SectorState.Pending);
            result.Bad = _map.Count(SectorState.Bad);
            result.RetriedThisRun = _retried;
            result.RecoveredThisRun = _recovered;

            _log.Section("Rescue result");
            if (_retried > 0)
                _log.Info("Retries this run: " + _recovered + " of " + _retried + " sector(s) recovered.");
            _log.Info(result.Finished ? "Nothing left to try." : result.Cancelled ? "Cancelled; run again to resume." :
                      result.Error != null ? "Stopped: " + result.Error : "Paused; run again to continue.");
            _log.Info("Disc: " + _map.Summary());
            _log.Info("Time this run: " + FormatTime(result.Elapsed));
            LogBadRanges();
            LogFileCoverage();
            if (result.Sha256 != null) _log.Info("SHA-256 of the image: " + result.Sha256);
            _log.Info("Map: " + _mapPath);
            return result;
        }

        // ------------------------------------------------------------ setup

        private string BaseName()
        {
            return BaseNameFor(_cfg, _probe);
        }

        private void LoadOrCreateMap(long total)
        {
            if (File.Exists(_mapPath))
            {
                string reason;
                SectorStateMap loaded = null;
                try { loaded = SectorStateMap.Load(_mapPath, total, out reason); }
                catch (Exception ex) { reason = ex.Message; }

                if (loaded != null && _cfg.WriteImage && !File.Exists(_imagePath) && loaded.Count(SectorState.Ok) > 0)
                {
                    reason = "the map says sectors were read, but the image " + _imageName + " is missing";
                    loaded = null;
                }
                if (loaded != null)
                {
                    _map = loaded;
                    _log.Info("Resuming from " + Path.GetFileName(_mapPath) + ": " + _map.Summary());
                    return;
                }
                string old = _mapPath + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(_mapPath, old);
                _log.Info("Existing map not usable (" + reason + "); renamed to " + Path.GetFileName(old) + ", starting over.");
            }
            _map = new SectorStateMap(total, _probe.VolumeLabel);
            _log.Info("New rescue: " + total + " sectors.");
        }

        private void OpenImage(long total)
        {
            if (!_cfg.WriteImage)
            {
                _log.Info("Read-only mode: the image is not written (data read in this mode is discarded).");
                return;
            }
            _image = new FileStream(_imagePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
            if (_image.Length != total * SectorSize)
            {
                // Reserve the full size up front: if space runs out, fail now and not hours later.
                // Sectors never written stay zero, which is exactly what the image must hold for them.
                _image.SetLength(total * SectorSize);
            }
            _log.Info("Image: " + _imagePath + " (" + DiscProbe.FormatSize(total) + ")");
        }

        private void CloseImage()
        {
            if (_image == null) return;
            try
            {
                _image.Flush(true);
                _image.Dispose();
            }
            catch (Exception ex)
            {
                _log.Info("Error closing the image: " + ex.Message);
            }
            _image = null;
        }

        private void SaveMap(string sha256)
        {
            try
            {
                // Image data first, then the map that claims it is there.
                if (_image != null) _image.Flush(true);
                _map.Save(_mapPath, _cfg.WriteImage ? _imageName : null, sha256);
                _lastMapSave = _sw.Elapsed;
            }
            catch (Exception ex)
            {
                _log.Info("Error saving the map: " + ex.Message);
            }
        }

        // ------------------------------------------------------------ passes

        private async Task FastPassAsync(string phase, long first, long end, bool allowSkip)
        {
            BeginPhase(phase, _map.CountIn(SectorState.Untried, first, end));
            var recentFailures = new Queue<long>();
            long carefulUntil = -1;
            long skip = FirstSkip;
            long lastFailure = long.MinValue;
            long pos = first;

            while ((pos = _map.NextWith(SectorState.Untried, pos, end)) < end)
            {
                long runEnd = _map.RunEnd(pos, end);
                while (pos < runEnd)
                {
                    _ct.ThrowIfCancellationRequested();
                    int n = pos < carefulUntil
                        ? (int)Math.Min(EccBlock - pos % EccBlock, runEnd - pos)
                        : (int)Math.Min(_cfg.SectorsPerRead, runEnd - pos);

                    var r = await ReadAsync(pos, n);
                    int failedPieces = 0;
                    if (Ok(r, n))
                    {
                        Store(pos, n);
                    }
                    else
                    {
                        // Split into ECC blocks and try each once: one bad ECC block should not take
                        // the good ones in the same read down with it.
                        for (long p = pos; p < pos + n; )
                        {
                            int m = (int)Math.Min(EccBlock - p % EccBlock, pos + n - p);
                            var rp = (m == n) ? r : await ReadAsync(p, m);
                            if (Ok(rp, m)) Store(p, m);
                            else
                            {
                                _map.Set(p, m, SectorState.Pending, ErrorText(rp));
                                _log.Info("LBA " + p + "-" + (p + m - 1) + " failed: " + rp.Describe() + " -> pending");
                                failedPieces++;
                            }
                            p += m;
                        }
                        carefulUntil = pos + n + CarefulSectors;
                    }

                    long next = pos + n;
                    _phaseDone += n;

                    if (failedPieces > 0)
                    {
                        lastFailure = pos;
                        for (int i = 0; i < failedPieces; i++) recentFailures.Enqueue(pos);
                        while (recentFailures.Count > 0 && recentFailures.Peek() < pos - DenseWindow) recentFailures.Dequeue();
                        if (allowSkip && recentFailures.Count >= DenseFailures)
                        {
                            long target = Math.Min(end, next + skip);
                            _log.Info("Dense errors around LBA " + pos + ": skipping " + (target - next) + " sectors to LBA " + target +
                                      " (they stay 'not tried' for the fill pass).");
                            next = target;
                            skip = Math.Min(MaxSkip, skip * 2);
                            recentFailures.Clear();
                            carefulUntil = next + CarefulSectors;
                        }
                    }
                    else if (pos - lastFailure > 2 * DenseWindow)
                    {
                        skip = FirstSkip;
                    }

                    pos = next;
                    Report(pos);
                    if (next > runEnd || next >= end) break; // jumped: find the next untried run again
                }
            }
            EndPhase();
        }

        /// <summary>One attempt per sector over every sector in the given state (scrape and retry passes).</summary>
        private async Task SectorPassAsync(string phase, SectorState target, long first, long end)
        {
            BeginPhase(phase, _map.CountIn(target, first, end));
            long recovered = 0;
            long pos = first;
            while ((pos = _map.NextWith(target, pos, end)) < end)
            {
                _ct.ThrowIfCancellationRequested();
                var r = await ReadAsync(pos, 1);
                if (Ok(r, 1))
                {
                    Store(pos, 1);
                    recovered++;
                    _log.Info("Sector " + pos + " recovered (" + phase + ").");
                }
                else
                {
                    _map.Set(pos, 1, SectorState.Bad, ErrorText(r));
                }
                _phaseDone++;
                pos++;
                Report(pos);
            }
            _log.Info(phase + ": recovered " + recovered + " of " + _phaseTotal + " sector(s).");
            _retried += _phaseTotal;
            _recovered += recovered;
            EndPhase();
        }

        // ------------------------------------------------------------ reading and writing

        private async Task<ScsiResult> ReadAsync(long lba, int count)
        {
            var r = await _drive.SendAsync(ScsiCommands.Read10((uint)lba, count), count * SectorSize, _buffer, 0,
                                           (uint)_cfg.TimeoutSeconds);
            if (r.Sense != null && r.Sense.IsNoMedium)
                throw new MediumRemovedException("Disc removed or no media in the drive: " + r.Sense);
            return r;
        }

        private static bool Ok(ScsiResult r, int count)
        {
            return r.Success && r.DataLength == count * SectorSize;
        }

        private static string ErrorText(ScsiResult r)
        {
            return r.Sense != null ? r.Sense.Code + " " + r.Sense.Describe() : r.Describe();
        }

        /// <summary>Writes the sectors in _buffer[0..] to the image at their position and marks them Ok.</summary>
        private void Store(long lba, int count)
        {
            if (_image != null)
            {
                _image.Seek(lba * SectorSize, SeekOrigin.Begin);
                _image.Write(_buffer, 0, count * SectorSize);
            }
            _map.Set(lba, count, SectorState.Ok, null);
        }

        // ------------------------------------------------------------ drive tuning (experimental)

        private async Task<bool> SetSpeedAsync(int kbps)
        {
            if (kbps <= 0) return false;
            var r = await _drive.SendAsync(ScsiCommands.SetCdSpeed(kbps), 0, null, 0, 10);
            _log.Info("SET CD SPEED " + (kbps == 0xFFFF ? "max" : kbps + " kB/s") + ": " + r.Describe());
            return r.Success;
        }

        /// <summary>
        /// Logs the drive's read error recovery page (MODE page 01h) and, if DriveReadRetryCount >= 0,
        /// sets its read retry count for the fast passes. Volatile (SP=0) and restored afterwards.
        /// </summary>
        private async Task LogAndApplyDriveRetriesAsync()
        {
            var buf = new byte[64];
            var r = await _drive.SendAsync(ScsiCommands.ModeSense10(0x01, buf.Length), buf.Length, buf, 0, 10);
            if (!r.Success || r.DataLength < 8 + 4)
            {
                _log.Info("MODE SENSE page 01h: " + r.Describe());
                return;
            }
            int bdl = Bytes.BE16(buf, 6);
            int p = 8 + bdl;
            if (p + 4 > r.DataLength || (buf[p] & 0x3F) != 0x01)
            {
                _log.Info("MODE SENSE page 01h: unexpected response");
                return;
            }
            int pageLen = buf[p + 1] + 2;
            if (p + pageLen > r.DataLength) pageLen = r.DataLength - p;
            _log.Info("Drive error recovery page: flags 0x" + buf[p + 2].ToString("X2") + ", read retry count " + buf[p + 3] +
                      (pageLen > 8 ? ", write retry count " + buf[p + 8] : ""));
            if (_cfg.DriveReadRetryCount < 0) return;

            _originalErrorPage = new byte[pageLen];
            Buffer.BlockCopy(buf, p, _originalErrorPage, 0, pageLen);
            var page = (byte[])_originalErrorPage.Clone();
            page[3] = (byte)_cfg.DriveReadRetryCount;
            if (!await ModeSelectPageAsync(page, "read retry count " + _cfg.DriveReadRetryCount))
                _originalErrorPage = null; // nothing to restore
        }

        private async Task RestoreDriveRetriesAsync()
        {
            if (_originalErrorPage == null) return;
            var page = _originalErrorPage;
            _originalErrorPage = null;
            await ModeSelectPageAsync(page, "restore read retry count " + page[3]);
        }

        private async Task<bool> ModeSelectPageAsync(byte[] page, string what)
        {
            // Parameter list = 8-byte mode header (all zero for MODE SELECT, no block descriptors) + page.
            var data = new byte[8 + page.Length];
            Buffer.BlockCopy(page, 0, data, 8, page.Length);
            data[8] &= 0x3F; // PS bit must be zero in MODE SELECT
            var r = await _drive.SendOutAsync(ScsiCommands.ModeSelect10(data.Length), data, 10);
            _log.Info("MODE SELECT (" + what + "): " + r.Describe());
            return r.Success;
        }

        // ------------------------------------------------------------ hashing

        private async Task<string> HashImageAsync(long total)
        {
            BeginPhase("Computing SHA-256 of the image", total);
            var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256).CreateHash();
            var chunk = new byte[1 << 20];
            using (var f = new FileStream(_imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                long remaining = total * SectorSize;
                while (remaining > 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    int n = await f.ReadAsync(chunk, 0, (int)Math.Min(chunk.Length, remaining));
                    if (n <= 0) throw new IOException("image shorter than expected");
                    hash.Append(chunk.AsBuffer(0, n));
                    remaining -= n;
                    _phaseDone = (total * SectorSize - remaining) / SectorSize;
                    Report(_phaseDone);
                }
            }
            EndPhase();
            return CryptographicBuffer.EncodeToHexString(hash.GetValueAndReset());
        }

        // ------------------------------------------------------------ progress

        private void BeginPhase(string phase, long total)
        {
            _phase = phase;
            _phaseDone = 0;
            _phaseTotal = total;
            _speed = new SpeedMeter(_sw.Elapsed);
            _lastPercentLogged = -1;
            _log.Section(phase);
            _log.Info(total.ToString("N0") + " sector(s) to handle. Disc: " + _map.Summary());
            Report(0);
        }

        private void EndPhase()
        {
            SaveMap(null);
            _log.Info("End of " + _phase + ". Disc: " + _map.Summary() + " | " + FormatTime(_sw.Elapsed));
        }

        private void Report(long lba)
        {
            var elapsed = _sw.Elapsed;
            _speed.Add(elapsed, _phaseDone);
            if (elapsed - _lastMapSave >= MapSaveInterval) SaveMap(null);

            int percent = _phaseTotal > 0 ? (int)(_phaseDone * 100 / _phaseTotal) : 100;
            if (percent != _lastPercentLogged && percent % 5 == 0)
            {
                _lastPercentLogged = percent;
                _log.Info(_phase + " " + percent + "% - LBA " + lba + " | " + (_speed.BytesPerSecond / 1048576.0).ToString("0.00") +
                          " MiB/s | " + _map.Summary() + " | " + FormatTime(elapsed));
            }

            if (elapsed - _lastReport < TimeSpan.FromMilliseconds(250) && _phaseDone < _phaseTotal) return;
            _lastReport = elapsed;
            var p = new RescueProgress
            {
                Phase = _phase, PhaseDone = _phaseDone, PhaseTotal = _phaseTotal, CurrentLba = lba,
                BytesPerSecond = _speed.BytesPerSecond, Elapsed = elapsed,
                Total = _map.TotalSectors, Ok = _map.Count(SectorState.Ok), Untried = _map.Count(SectorState.Untried),
                Pending = _map.Count(SectorState.Pending), Bad = _map.Count(SectorState.Bad),
            };
            if (_speed.BytesPerSecond > 0)
                p.Remaining = TimeSpan.FromSeconds((_phaseTotal - _phaseDone) * (double)SectorSize / _speed.BytesPerSecond);
            _progress.Report(p);
        }

        private void LogBadRanges()
        {
            int shown = 0, ranges = 0;
            long pos = 0;
            while (pos < _map.TotalSectors)
            {
                long e = _map.RunEnd(pos, _map.TotalSectors);
                var s = _map.Get(pos);
                if (s == SectorState.Bad || s == SectorState.Pending)
                {
                    ranges++;
                    if (shown++ < 40)
                        _log.Info("  " + (s == SectorState.Bad ? "bad     " : "pending ") + pos + "-" + (e - 1) + " (" + (e - pos) + ") " + _map.ErrorOf(pos));
                }
                pos = e;
            }
            if (ranges > 40) _log.Info("  ... " + (ranges - 40) + " more range(s); see the .map file.");
        }

        /// <summary>How much of each file is already in the image: tells which part of the movie is missing.</summary>
        private void LogFileCoverage()
        {
            if (_probe.Files == null || _probe.Files.Count == 0) return;
            _log.Info("Files (share of sectors read OK):");
            foreach (var f in _probe.Files)
            {
                long start = Math.Min(f.Lba, _map.TotalSectors), end = Math.Min(f.End, _map.TotalSectors);
                if (end <= start) continue;
                long ok = _map.CountIn(SectorState.Ok, start, end);
                long missing = (end - start) - ok;
                string line = "  " + f.Path.PadRight(24) + " " + (ok * 100.0 / (end - start)).ToString("0.00").PadLeft(6) + "%";
                if (missing > 0)
                {
                    long firstMissing = start;
                    while (firstMissing < end && _map.Get(firstMissing) == SectorState.Ok) firstMissing++;
                    line += "  missing " + missing + " sector(s), first at " + (firstMissing - f.Lba) * 100 / (end - start) + "% of the file";
                }
                _log.Info(line);
            }
        }

        public static string FormatTime(TimeSpan t)
        {
            return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        /// <summary>Average speed over the last ~15 s (steadier than instantaneous, more current than the overall average).</summary>
        private sealed class SpeedMeter
        {
            private readonly Queue<KeyValuePair<TimeSpan, long>> _samples = new Queue<KeyValuePair<TimeSpan, long>>();
            public double BytesPerSecond { get; private set; }

            public SpeedMeter(TimeSpan start)
            {
                _samples.Enqueue(new KeyValuePair<TimeSpan, long>(start, 0));
            }

            public void Add(TimeSpan at, long sectorsDone)
            {
                _samples.Enqueue(new KeyValuePair<TimeSpan, long>(at, sectorsDone));
                while (_samples.Count > 2 && at - _samples.Peek().Key > TimeSpan.FromSeconds(15)) _samples.Dequeue();
                var oldest = _samples.Peek();
                double secs = (at - oldest.Key).TotalSeconds;
                if (secs > 0.5) BytesPerSecond = (sectorsDone - oldest.Value) * (double)SectorSize / secs;
            }
        }
    }
}
