using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>No disc in the drive (02/3A): the copy stops; this is not a bad sector.</summary>
    internal sealed class MediumRemovedException : Exception
    {
        public MediumRemovedException(string message) : base(message) { }
    }

    internal sealed class FailedSector
    {
        public long Lba;
        public string Error;
    }

    /// <summary>
    /// Reads one block with error recovery:
    ///   1. read the whole block; if it fails, try again BlockRetries times;
    ///   2. if it keeps failing, read sector by sector, with 1 + SectorRetries attempts each;
    ///   3. a sector that does not read is zero-filled in the buffer and returned in the list (error map).
    /// Never throws because of a bad sector. It only stops if the disc leaves the drive or the copy is cancelled.
    /// </summary>
    internal sealed class SectorReader
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private static readonly List<FailedSector> NoFailures = new List<FailedSector>();

        private readonly OpticalDrive _drive;
        private readonly CopyConfig _cfg;
        private readonly Logger _log;

        public long Commands { get; private set; }
        public long RecoveredBlocks { get; private set; }
        public long RecoveredSectors { get; private set; }

        public SectorReader(OpticalDrive drive, CopyConfig cfg, Logger log)
        {
            _drive = drive;
            _cfg = cfg;
            _log = log;
        }

        public async Task<List<FailedSector>> ReadAsync(long lba, int count, byte[] buffer, CancellationToken ct)
        {
            uint timeout = (uint)_cfg.TimeoutSeconds;
            int blockAttempts = 1 + _cfg.BlockRetries;
            string range = lba + "-" + (lba + count - 1);

            for (int attempt = 1; attempt <= blockAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Commands++;
                var r = await _drive.SendAsync(ScsiCommands.Read10((uint)lba, count), count * SectorSize, buffer, 0, timeout);
                if (r.Success && r.DataLength == count * SectorSize)
                {
                    if (attempt > 1)
                    {
                        RecoveredBlocks++;
                        _log.Info("Block " + range + " read on attempt " + attempt + ".");
                    }
                    return NoFailures;
                }
                ThrowIfFatal(r);
                _log.Info("Block " + range + ": attempt " + attempt + "/" + blockAttempts + " failed: " + r.Describe());
            }

            _log.Info("Block " + range + ": reading sector by sector.");
            var failed = new List<FailedSector>();
            int sectorAttempts = 1 + _cfg.SectorRetries;
            for (int i = 0; i < count; i++)
            {
                long s = lba + i;
                ScsiResult last = null;
                bool ok = false;
                for (int attempt = 1; attempt <= sectorAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    Commands++;
                    last = await _drive.SendAsync(ScsiCommands.Read10((uint)s, 1), SectorSize, buffer, i * SectorSize, timeout);
                    if (last.Success && last.DataLength == SectorSize)
                    {
                        ok = true;
                        if (attempt > 1)
                        {
                            RecoveredSectors++;
                            _log.Info("Sector " + s + " read on attempt " + attempt + ".");
                        }
                        break;
                    }
                    ThrowIfFatal(last);
                }
                if (!ok)
                {
                    // Zeros in place of the sector: the image keeps its size and the offsets stay right.
                    Array.Clear(buffer, i * SectorSize, SectorSize);
                    string error = last.Sense != null ? last.Sense.Code + " " + last.Sense.Describe() : last.Describe();
                    failed.Add(new FailedSector { Lba = s, Error = error });
                    _log.Info("Sector " + s + " UNREADABLE after " + sectorAttempts + " attempts: " + last.Describe() + " -> zeros");
                }
            }
            return failed;
        }

        private static void ThrowIfFatal(ScsiResult r)
        {
            if (r.Sense != null && r.Sense.IsNoMedium)
                throw new MediumRemovedException("Disc removed or no media in the drive: " + r.Sense);
        }
    }
}
