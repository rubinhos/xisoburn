using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// Readability scan: reads one 16-sector ECC block at evenly spaced points across the whole disc,
    /// once each, and logs which ones read. Takes a few minutes and writes nothing.
    ///
    /// Why it exists: in the second rescue run, sectors that read fine in the first run (LBA 2,151,888+)
    /// failed, and every sample past LBA 2,152,064 failed. Two explanations fit:
    ///   (a) the disc really is unreadable on this drive past ~LBA 2.15M (e.g. layer 1 damage), or
    ///   (b) after hitting the defect, the DRIVE stays in a bad state and fails even good sectors.
    /// To tell them apart, after every failure the scan re-reads a reference block that is known to be
    /// good (in layer 0). If the reference also fails, it is the drive (b), and the scan logs how long
    /// it takes to recover. Run it right after a full power-off of the console, before any rescue.
    /// </summary>
    internal sealed class DiscScan
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private const int Block = 16;
        private const int Points = 80;

        private readonly OpticalDrive _drive;
        private readonly DiscProbe _probe;
        private readonly Logger _log;
        private readonly uint _timeout;

        public DiscScan(OpticalDrive drive, DiscProbe probe, Logger log, uint timeout)
        {
            _drive = drive;
            _probe = probe;
            _log = log;
            _timeout = timeout;
        }

        public async Task<string> RunAsync(IProgress<string> progress, CancellationToken ct)
        {
            long total = _probe.TotalSectors;
            var buf = new byte[Block * SectorSize];
            // Reference: a block in layer 0 that read fine in every run so far.
            long reference = Align(Math.Min(100000, total / 20));

            _log.Section("Readability scan");
            var rr = await Read(reference, buf);
            _log.Info("Reference LBA " + reference + ": " + rr.Describe());
            if (!rr.Success)
                _log.Info("WARNING: the reference block does not read now. The drive may already be in a bad state; power the console off fully and retry.");

            var points = new SortedSet<long>();
            for (int i = 0; i < Points; i++) points.Add(Align(total * i / Points));
            points.Add(Align(total - Block));
            if (_probe.Pfi != null && _probe.Pfi.NumberOfLayers > 1)
            {
                long l0 = _probe.Pfi.Layer0Sectors;
                points.Add(Align(l0 - Block));
                points.Add(Align(l0));
            }
            // Around the damaged area found on the test DVD (harmless on other discs).
            foreach (long p in new long[] { 2100000, 2140000, 2151952, 2152016, 2160000, 2200000 })
                if (p < total) points.Add(Align(p));

            int ok = 0, fail = 0, driveStuck = 0, n = 0;
            long lastOk = -1, firstFail = -1;
            var lines = new List<string>();
            foreach (long lba in points)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                progress.Report("Scan " + n + "/" + points.Count + " - LBA " + lba.ToString("N0") + " | ok " + ok + ", failed " + fail +
                                ", drive stuck " + driveStuck);
                var r = await Read(lba, buf);
                string where = Where(lba);
                if (r.Success)
                {
                    ok++;
                    lastOk = lba;
                    _log.Info(Pct(lba, total) + " LBA " + lba + where + ": OK (" + r.ElapsedMs.ToString("0") + " ms)");
                    continue;
                }

                fail++;
                if (firstFail < 0) firstFail = lba;
                _log.Info(Pct(lba, total) + " LBA " + lba + where + ": FAILED " + r.Describe());

                // Is it the disc or the drive? Re-read the known-good reference.
                var check = await Read(reference, buf);
                if (check.Success)
                {
                    _log.Info("      reference still reads (" + check.ElapsedMs.ToString("0") + " ms) -> this spot is unreadable, the drive is fine");
                    continue;
                }
                driveStuck++;
                _log.Info("      REFERENCE FAILS TOO: " + check.Describe() + " -> the drive is in a bad state");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool recovered = false;
                for (int attempt = 1; attempt <= 6 && !recovered; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _drive.SendAsync(ScsiCommands.TestUnitReady(), 0, null, 0, _timeout);
                    await Task.Delay(5000, ct);
                    var again = await Read(reference, buf);
                    recovered = again.Success;
                    _log.Info("      recovery attempt " + attempt + ": " + again.Describe());
                }
                _log.Info(recovered ? "      drive recovered after " + sw.Elapsed.TotalSeconds.ToString("0") + " s"
                                    : "      drive did NOT recover; power the console off fully before the next run");
                if (!recovered) break;
            }

            string summary = "Scan: " + ok + " OK, " + fail + " failed of " + n + " points; drive stuck " + driveStuck + " time(s)" +
                             (firstFail >= 0 ? "; first failure at LBA " + firstFail.ToString("N0") : "") +
                             (lastOk >= 0 ? "; last OK at LBA " + lastOk.ToString("N0") : "");
            _log.Section("Scan result");
            _log.Info(summary);
            if (driveStuck > 0)
                _log.Info("=> The DRIVE gets into a bad state after failures: the rescue must pause/recover after errors.");
            else if (fail > 0)
                _log.Info("=> Failures do not affect the reference: those areas are unreadable on this drive (disc damage).");
            return summary;
        }

        private async Task<ScsiResult> Read(long lba, byte[] buf)
        {
            return await _drive.SendAsync(ScsiCommands.Read10((uint)lba, Block), Block * SectorSize, buf, 0, _timeout);
        }

        private string Where(long lba)
        {
            string layer = "";
            if (_probe.Pfi != null && _probe.Pfi.NumberOfLayers > 1)
                layer = lba < _probe.Pfi.Layer0Sectors ? " [L0]" : " [L1]";
            var f = IsoFiles.FileAt(_probe.Files, lba);
            return layer + (f != null ? " " + f.Path : "");
        }

        private static long Align(long lba) { return Math.Max(0, lba - lba % Block); }

        private static string Pct(long lba, long total)
        {
            return ((lba * 100.0 / total).ToString("0.0") + "%").PadLeft(6);
        }
    }
}
