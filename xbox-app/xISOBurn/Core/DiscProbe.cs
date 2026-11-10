using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Core
{
    /// <summary>
    /// Detects the disc and decides how many sectors to copy.
    ///
    /// Size decision (found on the console, docs/how-it-works.md): on an OTP dual-layer DVD, the Xbox drive's READ
    /// CAPACITY returned "physical end sector - start + 1" (15,939,015 instead of 3,317,799).
    /// So for DVDs the primary source is READ DVD STRUCTURE (OTP calculation), cross-checked against
    /// the ISO9660 volume size. READ CAPACITY is only used for CD/BD or if the structure fails.
    /// </summary>
    internal sealed class DiscProbe
    {
        private const int SectorSize = OpticalDrive.SectorSize;

        public bool Ready;
        public string NotReadyReason = "";
        public int Profile = -1;
        public long ReadCapacitySectors = -1;
        public DvdPhysicalFormat Pfi;
        public int Cpst = -1;
        public string VolumeLabel = "";
        public long Iso9660Sectors = -1;
        public bool Udf;
        /// <summary>Blu-ray: AACS directory present / BD+ program present (from the UDF listing).</summary>
        public bool Aacs, Bdplus;
        public long TotalSectors = -1;
        public string TotalSource = "";
        public List<DiscFile> Files = new List<DiscFile>();
        public readonly List<string> Warnings = new List<string>();

        public static async Task<DiscProbe> RunAsync(OpticalDrive drive, uint timeout, Logger log)
        {
            var p = new DiscProbe();
            log.Section("Detecting the disc");

            // TEST UNIT READY, waiting for the disc to spin up if needed (up to ~20 s).
            for (int attempt = 1; attempt <= 20; attempt++)
            {
                var r = await drive.SendAsync(ScsiCommands.TestUnitReady(), 0, null, 0, timeout);
                if (r.Success) { p.Ready = true; break; }
                p.NotReadyReason = r.Describe();
                bool transient = r.Sense != null && ((r.Sense.Key == 0x02 && r.Sense.Asc == 0x04) || r.Sense.Key == 0x06);
                if (!transient) break;
                await Task.Delay(1000);
            }
            if (!p.Ready)
            {
                log.Info("Drive not ready: " + p.NotReadyReason);
                return p;
            }

            var buf = new byte[36];
            var rc = await drive.SendAsync(ScsiCommands.GetConfiguration(8), 8, buf, 0, timeout);
            if (rc.Success && rc.DataLength >= 8) p.Profile = Bytes.BE16(buf, 6);
            log.Info("Profile: " + (p.Profile >= 0 ? "0x" + p.Profile.ToString("X4") + " " + MmcProfiles.Name(p.Profile) : rc.Describe()));

            rc = await drive.SendAsync(ScsiCommands.ReadCapacity10(), 8, buf, 0, timeout);
            if (rc.Success && rc.DataLength >= 8) p.ReadCapacitySectors = (long)Bytes.BE32(buf, 0) + 1;
            log.Info("READ CAPACITY: " + (p.ReadCapacitySectors >= 0 ? p.ReadCapacitySectors + " sectors" : rc.Describe()));

            bool dvd = MmcProfiles.IsDvd(p.Profile);
            if (dvd || p.Profile < 0)
            {
                rc = await drive.SendAsync(ScsiCommands.ReadDvdStructure(0, 0x00, 36), 36, buf, 0, timeout);
                if (rc.Success) p.Pfi = DvdPhysicalFormat.Parse(buf, rc.DataLength);
                if (p.Pfi != null)
                    log.Info("DVD structure: " + p.Pfi.BookTypeName + ", " + p.Pfi.NumberOfLayers + " layer(s)" +
                             (p.Pfi.NumberOfLayers > 1 ? (p.Pfi.OppositeTrackPath ? " OTP" : " PTP") : "") +
                             ", " + p.Pfi.TotalSectors + " sectors, layer 0 = " + p.Pfi.Layer0Sectors);
                else
                    log.Info("DVD structure: " + rc.Describe());

                var cb = new byte[8];
                rc = await drive.SendAsync(ScsiCommands.ReadDvdStructure(0, 0x01, 8), 8, cb, 0, timeout);
                if (rc.Success && rc.DataLength >= 6) p.Cpst = cb[4];
                log.Info("Protection (CPST): " + (p.Cpst >= 0 ? CpstName(p.Cpst) : rc.Describe()));
            }

            // File system: ISO9660 PVD at sector 16 and UDF anchor at sector 256.
            var sec = new byte[SectorSize];
            rc = await drive.SendAsync(ScsiCommands.Read10(16, 1), SectorSize, sec, 0, timeout);
            if (rc.Success && sec[0] == 1 && Bytes.Ascii(sec, 1, 5) == "CD001")
            {
                p.VolumeLabel = Bytes.Ascii(sec, 40, 32);
                p.Iso9660Sectors = Bytes.LE32(sec, 80);
                log.Info("ISO9660: \"" + p.VolumeLabel + "\", " + p.Iso9660Sectors + " blocks");
                try
                {
                    p.Files = await IsoFiles.ReadAsync(drive, (byte[])sec.Clone(), timeout);
                    log.Info("Files (" + p.Files.Count + "):");
                    foreach (var f in p.Files)
                        log.Info("  " + f.Path.PadRight(24) + " LBA " + f.Lba.ToString().PadLeft(8) + "-" + (f.End - 1).ToString().PadRight(8) +
                                 " " + (f.Size / 1048576.0).ToString("0.0").PadLeft(7) + " MiB");
                }
                catch (Exception ex)
                {
                    log.Info("Could not list the files: " + ex.Message);
                }
            }
            rc = await drive.SendAsync(ScsiCommands.Read10(256, 1), SectorSize, sec, 0, timeout);
            if (rc.Success && Bytes.LE16(sec, 0) == 2)
            {
                p.Udf = true;
                log.Info("UDF: anchor (AVDP) at sector 256");
            }

            // Blu-ray: the files come from UDF (there is usually no ISO9660), and tell AACS / BD+ apart.
            if (p.Udf && MmcProfiles.IsBluray(p.Profile))
            {
                try
                {
                    var udf = await Bluray.BluRaySession.ReadFileSystemAsync(drive, timeout, log);
                    if (udf != null)
                    {
                        if (p.VolumeLabel.Length == 0 && !string.IsNullOrEmpty(udf.VolumeId)) p.VolumeLabel = udf.VolumeId;
                        if (p.Files.Count == 0)
                            foreach (var f in udf.Files)
                                if (f.Extents.Count > 0)
                                    p.Files.Add(new DiscFile { Path = f.Path, Lba = f.Extents[0].Lba, Size = f.Extents[0].Length });
                        p.Files.Sort((a, b) => a.Lba.CompareTo(b.Lba));
                        p.Aacs = udf.Find("AACS/Unit_Key_RO.inf") != null;
                        p.Bdplus = udf.Find("BDSVM/00000.svm") != null;
                        log.Info("UDF: \"" + udf.VolumeId + "\", " + udf.Files.Count + " files" +
                                 (p.Aacs ? ", AACS" : ", no AACS") + (p.Bdplus ? ", BD+" : "") + ".");
                    }
                }
                catch (Exception ex)
                {
                    log.Info("UDF: could not list the files: " + ex.Message);
                }
            }

            p.DecideSize();
            log.Info("Size to copy: " + p.TotalSectors + " sectors (" + FormatSize(p.TotalSectors) + "), source: " + p.TotalSource);
            foreach (var w in p.Warnings) log.Info("WARNING: " + w);
            return p;
        }

        private void DecideSize()
        {
            long structure = -1;
            if (Pfi != null)
            {
                if (Pfi.NumberOfLayers == 1 || Pfi.OppositeTrackPath) structure = Pfi.TotalSectors;
                else Warnings.Add("PTP dual-layer DVD: the structure size does not cover layer 1; using READ CAPACITY.");
            }

            if (structure > 0)
            {
                TotalSectors = structure;
                TotalSource = "DVD structure";
                if (ReadCapacitySectors > 0 && ReadCapacitySectors != structure)
                    Warnings.Add("READ CAPACITY (" + ReadCapacitySectors + ") differs from the structure (" + structure +
                                 "). Expected for an OTP DVD-9 on this drive; the structure wins.");
            }
            else if (ReadCapacitySectors > 0)
            {
                TotalSectors = ReadCapacitySectors;
                TotalSource = "READ CAPACITY";
            }
            else if (Iso9660Sectors > 0)
            {
                TotalSectors = Iso9660Sectors;
                TotalSource = "ISO9660 volume";
            }

            if (Iso9660Sectors > 0 && TotalSectors > 0 && Iso9660Sectors != TotalSectors)
            {
                Warnings.Add("The ISO9660 volume says " + Iso9660Sectors + " sectors and the disc " + TotalSectors + ".");
                if (Iso9660Sectors > TotalSectors)
                {
                    // Better to copy too much (the drive refuses past the end and that goes into the map) than to cut data.
                    TotalSectors = Iso9660Sectors;
                    TotalSource += " (extended to the ISO9660 size)";
                }
            }
        }

        public IEnumerable<string> Describe()
        {
            if (!Ready)
            {
                yield return "No disc ready: " + NotReadyReason;
                yield break;
            }
            string media = Profile >= 0 ? MmcProfiles.Name(Profile) : "unknown media";
            if (Pfi != null && Pfi.NumberOfLayers > 1)
                media += ", 2 layers (" + (Pfi.OppositeTrackPath ? "OTP" : "PTP") + ")";
            else if (Pfi != null)
                media += ", 1 layer";
            yield return media;
            yield return "Volume: " + (VolumeLabel.Length > 0 ? VolumeLabel : "(no ISO9660)") + (Udf ? " | UDF" : "");
            if (MmcProfiles.IsBluray(Profile) && Udf)
                yield return "Protection: " + (Aacs ? "AACS" + (Bdplus ? " + BD+" : "") + " (decrypted while copying with the keys uploaded on the web page)" : "none") +
                             (Files.Count > 0 ? " | " + Files.Count + " files" : "");
            yield return "Size: " + TotalSectors.ToString("N0") + " sectors (" + FormatSize(TotalSectors) + ")" +
                         (Pfi != null && Pfi.NumberOfLayers > 1 ? " | layer change at LBA " + Pfi.Layer0Sectors.ToString("N0") : "");
            if (Cpst >= 0)
                yield return "Protection: " + CpstName(Cpst) + (Cpst == 1 ? " (the image may come out scrambled)" : "") +
                             (Files.Count > 0 ? " | " + Files.Count + " files" : "") +
                             (Warnings.Count > 0 ? " | " + Warnings.Count + " warning(s) in the log" : "");
        }

        private static string CpstName(int cpst)
        {
            switch (cpst)
            {
                case 0: return "none";
                case 1: return "CSS/CPPM";
                case 2: return "CPRM";
                default: return "code 0x" + cpst.ToString("X2");
            }
        }

        public static string FormatSize(long sectors)
        {
            if (sectors < 0) return "?";
            return (sectors * (double)SectorSize / 1073741824.0).ToString("0.00") + " GiB";
        }
    }
}
