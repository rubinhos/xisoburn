using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using xISOBurn.Scsi;
using xISOBurn.Util;

namespace xISOBurn.Spike
{
    /// <summary>
    /// Milestone 1: feasibility test. It only READS information and sectors; it writes nothing to the disc/drive.
    /// Each step is independent: if one fails the rest still runs, so the log is as complete as possible.
    /// </summary>
    internal sealed class SpikeRunner
    {
        private const int SectorSize = OpticalDrive.SectorSize;
        private const uint Timeout = 30;          // seconds per command (a bad sector can take a while)
        private const int SafeChunk = 16;         // sectors per READ(10) for the "safe" reads
        private const int FirstSectors = 1000;    // as requested for milestone 1
        private static readonly int[] TransferTests = { 16, 31, 32, 48, 64 };

        private readonly Logger _log;
        private readonly string _folder;
        private OpticalDrive _drive;

        // Results kept for the final summary.
        private int _profile = -1;
        private long _capacity = -1;
        private DvdPhysicalFormat _pfi0, _pfi1;
        private int _cpst = -1;
        private readonly List<string> _summary = new List<string>();

        public SpikeRunner(Logger log, string localFolder)
        {
            _log = log;
            _folder = localFolder;
        }

        public async Task RunAsync()
        {
            _log.Info("xISOBurn - feasibility test (milestone 1)");
            _log.Info("Log: " + _log.FilePath);

            if (!await OpenDriveAsync()) return;
            try
            {
                await Step("INQUIRY", InquiryAsync);
                bool ready = false;
                await Step("TEST UNIT READY", async () => { ready = await TestUnitReadyAsync(); });
                if (!ready)
                {
                    _log.Info("The drive did not become ready. Insert the disc, wait for it to stop spinning up and run again.");
                    _log.Info("(The next steps will still be attempted.)");
                }
                await Step("GET CONFIGURATION (media type)", GetConfigurationAsync);
                await Step("READ CAPACITY(10)", ReadCapacityAsync);
                await Step("READ DVD STRUCTURE format 0 (Physical Format)", ReadPhysicalFormatAsync);
                await Step("READ DVD STRUCTURE format 1 (Copyright)", ReadCopyrightAsync);
                await Step("REPORT KEY format 8 (drive RPC region state)", ReportRpcStateAsync);
                if (MmcProfiles.IsCd(_profile))
                    await Step("READ TOC (CD)", ReadTocAsync);
                CompareSizes();
                await Step("Transfer size test", TransferSizeTestAsync);
                byte[] first = null;
                await Step("Reading the first " + FirstSectors + " sectors", async () => { first = await ReadFirstSectorsAsync(); });
                if (first != null)
                    await Step("File system (ISO9660/UDF) and hex dump", () => { AnalyzeFileSystem(first); return Task.FromResult(0); });
                await Step("Sample reads (across the disc, layer change, end)", SampleReadsAsync);
            }
            finally
            {
                _drive.Dispose();
            }

            _log.Section("SUMMARY");
            foreach (var s in _summary) _log.Info(s);
            _log.Info("");
            _log.Info("Test finished. Download the log from the Device Portal (File explorer > LocalAppData > xISOBurn_... > LocalState).");
        }

        private async Task Step(string title, Func<Task> body)
        {
            _log.Section(title);
            try { await body(); }
            catch (Exception ex)
            {
                _log.Info("EXCEPTION: 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
                _summary.Add(title + ": EXCEPTION " + ex.Message);
            }
        }

        // ---------------------------------------------------------------- opening

        private async Task<bool> OpenDriveAsync()
        {
            _log.Section("Looking for the drive (DeviceInformation.FindAllAsync)");
            _log.Info("AQS filter: " + OpticalDrive.Selector);
            IReadOnlyList<Windows.Devices.Enumeration.DeviceInformation> devices;
            try
            {
                devices = await OpticalDrive.FindAsync();
            }
            catch (Exception ex)
            {
                _log.Info("Enumeration FAILED: 0x" + ex.HResult.ToString("X8") + " " + ex.Message);
                _summary.Add("Enumeration: FAILED (" + ex.Message + ")");
                return false;
            }

            _log.Info("Interfaces found: " + devices.Count);
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                object enabled;
                d.Properties.TryGetValue("System.Devices.InterfaceEnabled", out enabled);
                _log.Info("  [" + i + "] name=\"" + d.Name + "\" IsEnabled=" + d.IsEnabled + " InterfaceEnabled=" + (enabled ?? "?"));
                _log.Info("      id=" + d.Id);
            }
            if (devices.Count == 0)
            {
                _log.Info("No CD-ROM interface. Console without a drive (Series S/Digital) or capability denied.");
                _summary.Add("Drive: NOT FOUND");
                return false;
            }

            _log.Section("Opening the drive (CustomDevice.FromIdAsync, ReadWrite, Shared)");
            // Like XBSX2: use the first interface. Buffers for up to 64 sectors (128 KiB) for the
            // transfer size test; XBSX2 uses 64 KiB and caps each command at 31 sectors.
            try
            {
                _drive = await OpticalDrive.OpenAsync(devices[0].Id, 64 * SectorSize);
            }
            catch (Exception ex)
            {
                _log.Info("Open FAILED: 0x" + ex.HResult.ToString("X8") + " " + ex.GetType().Name + ": " + ex.Message);
                _summary.Add("Open drive: FAILED (0x" + ex.HResult.ToString("X8") + ")");
                return false;
            }
            _log.Info("Opened. IOCTL code = 0x" + _drive.ControlCode.ToString("X8") + " (expected 0x0004D004 = IOCTL_SCSI_PASS_THROUGH)");
            _summary.Add("Drive: opened (" + devices.Count + " interface(s))");
            return true;
        }

        // ---------------------------------------------------------------- information

        private async Task InquiryAsync()
        {
            var buf = new byte[36];
            var r = await _drive.SendAsync(ScsiCommands.Inquiry(36), 36, buf, 0, Timeout);
            _log.Info("Result: " + r.Describe());
            if (!r.Success || r.DataLength < 36) return;
            string vendor = Bytes.Ascii(buf, 8, 8), product = Bytes.Ascii(buf, 16, 16), rev = Bytes.Ascii(buf, 32, 4);
            _log.Info("Peripheral type: 0x" + (buf[0] & 0x1F).ToString("X2") + " (0x05 = CD/DVD)");
            _log.Info("Vendor: " + vendor + " | Model: " + product + " | Firmware: " + rev);
            _summary.Add("Drive: " + vendor + " " + product + " fw " + rev);
        }

        private async Task<bool> TestUnitReadyAsync()
        {
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                var r = await _drive.SendAsync(ScsiCommands.TestUnitReady(), 0, null, 0, Timeout);
                _log.Info("Attempt " + attempt + ": " + r.Describe());
                if (r.Success)
                {
                    _summary.Add("TEST UNIT READY: ready");
                    return true;
                }
                // Only wait on "transient" cases: disc spinning up, or a media change/reset notice.
                bool transient = r.Sense != null &&
                    ((r.Sense.Key == 0x02 && r.Sense.Asc == 0x04) || r.Sense.Key == 0x06);
                if (!transient) break;
                await Task.Delay(1000);
            }
            _summary.Add("TEST UNIT READY: NOT ready");
            return false;
        }

        private async Task GetConfigurationAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.GetConfiguration(8), 8, buf, 0, Timeout);
            _log.Info("Result: " + r.Describe());
            if (!r.Success || r.DataLength < 8) return;
            _profile = Bytes.BE16(buf, 6);
            _log.Info("Current profile: 0x" + _profile.ToString("X4") + " = " + MmcProfiles.Name(_profile));
            _summary.Add("Media: " + MmcProfiles.Name(_profile) + " (profile 0x" + _profile.ToString("X4") + ")");
        }

        private async Task ReadCapacityAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.ReadCapacity10(), 8, buf, 0, Timeout);
            _log.Info("Result: " + r.Describe());
            if (!r.Success || r.DataLength < 8) return;
            uint lastLba = Bytes.BE32(buf, 0);
            uint blockLen = Bytes.BE32(buf, 4);
            _capacity = (long)lastLba + 1;
            _log.Info("Last LBA: " + lastLba + " | Block size: " + blockLen + " bytes");
            _log.Info("Capacity: " + _capacity + " sectors = " + FormatBytes(_capacity * SectorSize));
            if (blockLen != SectorSize)
                _log.Info("WARNING: block size is not 2048 bytes.");
            _summary.Add("READ CAPACITY: " + _capacity + " sectors (" + FormatBytes(_capacity * SectorSize) + ")");
        }

        private async Task ReadPhysicalFormatAsync()
        {
            _pfi0 = await ReadPfiLayerAsync(0);
            if (_pfi0 == null) return;
            if (_pfi0.NumberOfLayers > 1)
            {
                // Layer 1 only has a meaningful descriptor of its own on PTP, but we ask anyway to log it.
                _log.Info("");
                _log.Info("Two-layer disc: reading the layer 1 descriptor.");
                _pfi1 = await ReadPfiLayerAsync(1);
            }

            string layers = _pfi0.NumberOfLayers == 1 ? "1 layer"
                : "2 layers, " + (_pfi0.OppositeTrackPath ? "OTP" : "PTP");
            _summary.Add("DVD: " + _pfi0.BookTypeName + ", " + layers + ", " + _pfi0.TotalSectors + " sectors by structure" +
                (_pfi0.NumberOfLayers > 1 ? ", layer 0 = " + _pfi0.Layer0Sectors + " sectors" : ""));
        }

        private async Task<DvdPhysicalFormat> ReadPfiLayerAsync(byte layer)
        {
            // 36 bytes, as in XBSX2 (4-byte header + 17-byte descriptor + slack).
            var buf = new byte[36];
            var r = await _drive.SendAsync(ScsiCommands.ReadDvdStructure(layer, 0x00, buf.Length), buf.Length, buf, 0, Timeout);
            _log.Info("Layer " + layer + ": " + r.Describe());
            if (!r.Success) return null;
            foreach (var line in HexDump.Lines(buf, 0, r.DataLength, 0)) _log.Info("  " + line);
            var p = DvdPhysicalFormat.Parse(buf, r.DataLength);
            if (p == null) { _log.Info("Response too short to parse."); return null; }
            _log.Info("  Book type: " + p.BookTypeName + " (version " + p.PartVersion + ")");
            _log.Info("  Size: " + (p.DiscSize == 0 ? "120 mm" : p.DiscSize == 1 ? "80 mm" : "code " + p.DiscSize) +
                      " | Max rate: " + p.MaxRateName);
            _log.Info("  Layers: " + p.NumberOfLayers + " | Track path: " + (p.OppositeTrackPath ? "OTP (opposite)" : "PTP (parallel)") +
                      " | Layer type: 0x" + p.LayerType.ToString("X"));
            _log.Info("  Physical start sector: 0x" + p.StartSector.ToString("X6") +
                      " | end: 0x" + p.EndSector.ToString("X6") +
                      " | end of layer 0: 0x" + p.EndSectorLayer0.ToString("X6"));
            _log.Info("  BCA: " + (p.Bca ? "yes" : "no"));
            _log.Info("  => sectors in layer 0: " + p.Layer0Sectors + " | computed total: " + p.TotalSectors);
            return p;
        }

        private async Task ReadCopyrightAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.ReadDvdStructure(0, 0x01, buf.Length), buf.Length, buf, 0, Timeout);
            _log.Info("Result: " + r.Describe());
            if (!r.Success || r.DataLength < 6) return;
            _cpst = buf[4];
            int rmi = buf[5];
            string system;
            switch (_cpst)
            {
                case 0x00: system = "none"; break;
                case 0x01: system = "CSS/CPPM"; break;
                case 0x02: system = "CPRM"; break;
                default: system = "code 0x" + _cpst.ToString("X2"); break;
            }
            _log.Info("Disc protection (CPST): " + system);
            _log.Info("Disc regions (RMI 0x" + rmi.ToString("X2") + "): " + RegionList(rmi));
            _summary.Add("Protection: " + system + " | disc regions: " + RegionList(rmi));
        }

        private async Task ReportRpcStateAsync()
        {
            var buf = new byte[8];
            var r = await _drive.SendAsync(ScsiCommands.ReportKeyRpcState(), buf.Length, buf, 0, Timeout);
            _log.Info("Result: " + r.Describe());
            if (!r.Success || r.DataLength < 7) return;
            int typeCode = buf[4] >> 6;
            int vendorResets = (buf[4] >> 3) & 0x07;
            int userChanges = buf[4] & 0x07;
            int regionMask = buf[5];
            int scheme = buf[6];
            string[] types = { "region not set", "region set", "last change available", "region permanent" };
            _log.Info("State: " + types[typeCode] + " | user changes left: " + userChanges + " | vendor resets: " + vendorResets);
            _log.Info("Drive region (mask 0x" + regionMask.ToString("X2") + "): " + RegionList(regionMask));
            _log.Info("RPC scheme: " + (scheme == 1 ? "RPC Phase II" : scheme == 0 ? "unknown/RPC-1" : "code " + scheme));
            _summary.Add("Drive region: " + RegionList(regionMask) + " (" + types[typeCode] + ")");
        }

        private async Task ReadTocAsync()
        {
            var buf = new byte[804];
            var r = await _drive.SendAsync(ScsiCommands.ReadToc(buf.Length), buf.Length, buf, 0, Timeout);
            _log.Info("Result: " + r.Describe());
            if (!r.Success || r.DataLength < 4) return;
            int len = Math.Min(r.DataLength, Bytes.BE16(buf, 0) + 2);
            _log.Info("Tracks " + buf[2] + " to " + buf[3]);
            for (int o = 4; o + 8 <= len; o += 8)
            {
                int track = buf[o + 2];
                bool data = (buf[o + 1] & 0x04) != 0;
                uint lba = Bytes.BE32(buf, o + 4);
                _log.Info("  Track " + (track == 0xAA ? "lead-out" : track.ToString()) + ": LBA " + lba + (data ? " (data)" : " (audio)"));
            }
            _log.Info("Note: in milestone 1, CDs are read with READ(10) only (Mode 1). Mode 2 needs READ CD.");
        }

        private void CompareSizes()
        {
            _log.Section("Size comparison");
            if (_capacity < 0 || _pfi0 == null)
            {
                _log.Info("Missing data (READ CAPACITY or DVD structure) to compare.");
                return;
            }
            long structure = _pfi0.TotalSectors;
            if (_pfi0.NumberOfLayers > 1 && !_pfi0.OppositeTrackPath && _pfi1 != null)
                structure = _pfi0.TotalSectors + _pfi1.TotalSectors; // PTP: add both layers
            _log.Info("READ CAPACITY: " + _capacity + " | DVD structure: " + structure + " | difference: " + (_capacity - structure));
            string verdict = _capacity == structure ? "equal" : "DIFFERENT (see log)";
            _summary.Add("Sizes READ CAPACITY vs structure: " + verdict);

            // On the Xbox drive (PLDS DG-6M5S), READ CAPACITY for an OTP DVD-9 came back as
            // "end sector - start sector + 1", ignoring layer 1's complemented numbering, i.e. larger
            // than the disc. The structure is the reliable source; the samples use it.
            if (structure > 0 && structure != _capacity && (_pfi0.NumberOfLayers == 1 || _pfi0.OppositeTrackPath))
            {
                _log.Info("Using the DVD structure size (" + structure + ") for the next steps.");
                _capacity = structure;
            }
        }

        // ---------------------------------------------------------------- reads

        private async Task<ScsiResult> ReadAsync(uint lba, int count, byte[] dest, int destOffset)
        {
            return await _drive.SendAsync(ScsiCommands.Read10(lba, count), count * SectorSize, dest, destOffset, Timeout);
        }

        /// <summary>
        /// Finds out how many sectors fit in a single READ(10) through pass-through. XBSX2 uses at most 31
        /// (64 KiB minus the header); we want to know whether 32 (exactly 64 KiB) or more work.
        /// Each read uses a different LBA so we do not just measure the drive cache. It also checks that a
        /// large read returns the same bytes as smaller reads of the same range.
        /// </summary>
        private async Task TransferSizeTestAsync()
        {
            var big = new byte[64 * SectorSize];
            var small = new byte[64 * SectorSize];
            var ok = new List<string>();
            uint lba = 2000;
            foreach (int n in TransferTests)
            {
                if (_capacity > 0 && lba + n > _capacity) break;
                var r = await ReadAsync(lba, n, big, 0);
                string line = n + " sectors @ LBA " + lba + ": " + r.Describe();
                if (r.Success)
                {
                    double mbps = r.ElapsedMs > 0 ? (n * SectorSize / 1048576.0) / (r.ElapsedMs / 1000.0) : 0;
                    line += " ~" + mbps.ToString("0.0") + " MiB/s";
                    // Compare against 16-sector reads of the same range.
                    bool same = true;
                    for (int done = 0; done < n && same; done += SafeChunk)
                    {
                        int c = Math.Min(SafeChunk, n - done);
                        var rs = await ReadAsync(lba + (uint)done, c, small, done * SectorSize);
                        if (!rs.Success) { line += " | check read failed: " + rs.Describe(); same = false; break; }
                    }
                    if (same)
                    {
                        same = Bytes.Equal(big, 0, small, 0, n * SectorSize);
                        line += same ? " | content matches" : " | CONTENT DIFFERS from the 16-sector reads";
                    }
                    if (r.DataLength == n * SectorSize && same) ok.Add(n.ToString());
                }
                _log.Info(line);
                lba += 64;
            }
            _summary.Add("Sectors per command that worked: " + (ok.Count > 0 ? string.Join(", ", ok) : "none"));
        }

        /// <summary>
        /// Reads the first sectors in blocks of 16. If a block fails, it reads sector by sector to isolate
        /// the problem. Failed sectors stay zero. Everything is saved to LocalState for analysis on the PC.
        /// </summary>
        private async Task<byte[]> ReadFirstSectorsAsync()
        {
            int total = FirstSectors;
            if (_capacity > 0 && _capacity < total) total = (int)_capacity;
            var data = new byte[total * SectorSize];
            var failures = new List<string>();
            int copyProtection = 0, mediumErrors = 0, otherErrors = 0;
            var sw = Stopwatch.StartNew();

            for (int lba = 0; lba < total; lba += SafeChunk)
            {
                int n = Math.Min(SafeChunk, total - lba);
                var r = await ReadAsync((uint)lba, n, data, lba * SectorSize);
                if (r.Success && r.DataLength == n * SectorSize) continue;

                _log.Info("Block LBA " + lba + "-" + (lba + n - 1) + " failed: " + r.Describe() + " -> reading sector by sector");
                Array.Clear(data, lba * SectorSize, n * SectorSize);
                for (int s = lba; s < lba + n; s++)
                {
                    var rs = await ReadAsync((uint)s, 1, data, s * SectorSize);
                    if (rs.Success && rs.DataLength == SectorSize) continue;
                    Array.Clear(data, s * SectorSize, SectorSize);
                    failures.Add("  LBA " + s + ": " + rs.Describe());
                    if (rs.Sense != null && rs.Sense.IsCopyProtection) copyProtection++;
                    else if (rs.Sense != null && rs.Sense.IsMediumError) mediumErrors++;
                    else otherErrors++;
                }
            }
            sw.Stop();

            double secs = sw.Elapsed.TotalSeconds;
            _log.Info("Read " + total + " sectors in " + secs.ToString("0.00") + " s (~" +
                      (total * SectorSize / 1048576.0 / Math.Max(secs, 0.001)).ToString("0.0") + " MiB/s)");
            _log.Info("Failed sectors: " + failures.Count + " (protection/CSS: " + copyProtection +
                      ", medium error: " + mediumErrors + ", other: " + otherErrors + ")");
            int shown = 0;
            foreach (var f in failures)
            {
                if (shown++ >= 50) { _log.Info("  ... (" + (failures.Count - 50) + " more)"); break; }
                _log.Info(f);
            }

            string path = Path.Combine(_folder, "spike-first-" + total + "-sectors.bin");
            File.WriteAllBytes(path, data);
            _log.Info("Saved to " + path + " (failed sectors = zeros)");

            _summary.Add("First " + total + " sectors: " + (total - failures.Count) + " OK, " + failures.Count + " failed" +
                (copyProtection > 0 ? " (" + copyProtection + " refused by CSS protection)" : ""));
            return data;
        }

        private void AnalyzeFileSystem(byte[] data)
        {
            int sectors = data.Length / SectorSize;

            // Sectors 0-15: the "system area", usually all zeros on DVD-Video.
            _log.Info("Sectors 0-15 (system area): " + (Bytes.AllZero(data, 0, Math.Min(16, sectors) * SectorSize) ? "all zero" : "contain data"));

            // Volume Recognition Sequence from sector 16: ISO9660 "CD001", UDF "BEA01"/"NSR02"/"NSR03"/"TEA01".
            var ids = new List<string>();
            for (int s = 16; s < Math.Min(32, sectors); s++)
            {
                string id = Bytes.Ascii(data, s * SectorSize + 1, 5);
                if (id == "CD001" || id == "BEA01" || id == "NSR02" || id == "NSR03" || id == "TEA01" || id == "BOOT2" || id == "CDW02")
                    ids.Add(s + ":" + id + "(type " + data[s * SectorSize] + ")");
            }
            _log.Info("Volume identifiers (sectors 16-31): " + (ids.Count > 0 ? string.Join(" ", ids) : "none"));

            bool iso = sectors > 16 && Bytes.Ascii(data, 16 * SectorSize + 1, 5) == "CD001" && data[16 * SectorSize] == 1;
            if (iso)
            {
                int o = 16 * SectorSize;
                string volumeId = Bytes.Ascii(data, o + 40, 32);
                uint volumeBlocks = Bytes.LE32(data, o + 80);
                _log.Info("ISO9660 PVD: volume \"" + volumeId + "\", " + volumeBlocks + " blocks of " + Bytes.LE16(data, o + 128) + " bytes");
                _summary.Add("ISO9660: \"" + volumeId + "\", " + volumeBlocks + " blocks" +
                    (_capacity > 0 ? (volumeBlocks == _capacity ? " (= capacity)" : " (capacity " + _capacity + ")") : ""));
            }

            // UDF: Anchor Volume Descriptor Pointer at sector 256, tag identifier 2 (little-endian).
            if (sectors > 256)
            {
                int o = 256 * SectorSize;
                ushort tag = Bytes.LE16(data, o);
                _log.Info("Sector 256: UDF tag = " + tag + (tag == 2 ? " (Anchor Volume Descriptor Pointer: UDF present)" : ""));
                if (tag == 2) _summary.Add("UDF: AVDP found at sector 256");
            }

            DumpSector(data, 0, 64);
            DumpSector(data, 16, 128);
            DumpSector(data, 17, 64);
            DumpSector(data, 256, 64);

            // First sector that looks like an MPEG pack (VOB) within the first sectors, if any.
            for (int s = 0; s < sectors; s++)
            {
                if (IsMpegPack(data, s * SectorSize))
                {
                    _log.Info("First MPEG-PS pack (VOB) in the first sectors: LBA " + s +
                              (IsScrambledPack(data, s * SectorSize) ? " - flagged SCRAMBLED (CSS)" : " - not scrambled"));
                    DumpSector(data, s, 64);
                    break;
                }
            }
        }

        /// <summary>
        /// Reads ranges spread across the disc. The first 1000 sectors almost never hold video; the VOBs
        /// (where CSS applies) come later. This answers what only the console can: does the drive return
        /// video sectors without authentication? Does it read layer 1 and across the layer change?
        /// </summary>
        private async Task SampleReadsAsync()
        {
            if (_capacity <= 0)
            {
                _log.Info("Capacity unknown; skipping.");
                return;
            }
            var points = new List<KeyValuePair<string, long>>();
            points.Add(new KeyValuePair<string, long>("10%", _capacity / 10));
            points.Add(new KeyValuePair<string, long>("25%", _capacity / 4));
            points.Add(new KeyValuePair<string, long>("50%", _capacity / 2));
            points.Add(new KeyValuePair<string, long>("75%", _capacity * 3 / 4));
            if (_pfi0 != null && _pfi0.NumberOfLayers > 1)
            {
                long l0 = _pfi0.Layer0Sectors;
                points.Add(new KeyValuePair<string, long>("end of layer 0", l0 - SafeChunk));
                points.Add(new KeyValuePair<string, long>("start of layer 1", l0));
            }
            points.Add(new KeyValuePair<string, long>("end of disc", _capacity - SafeChunk));

            var buf = new byte[SafeChunk * SectorSize];
            int okCount = 0, cssRefused = 0, otherFail = 0, scrambled = 0, packs = 0;
            foreach (var p in points)
            {
                long lba = Math.Max(0, Math.Min(p.Value, _capacity - SafeChunk));
                var r = await ReadAsync((uint)lba, SafeChunk, buf, 0);
                string line = p.Key + " (LBA " + lba + "-" + (lba + SafeChunk - 1) + "): " + r.Describe();
                if (r.Success)
                {
                    okCount++;
                    int pk = 0, sc = 0, zero = 0;
                    for (int s = 0; s < SafeChunk; s++)
                    {
                        if (Bytes.AllZero(buf, s * SectorSize, SectorSize)) zero++;
                        if (IsMpegPack(buf, s * SectorSize)) { pk++; if (IsScrambledPack(buf, s * SectorSize)) sc++; }
                    }
                    packs += pk; scrambled += sc;
                    line += " | MPEG packs: " + pk + ", scrambled: " + sc + ", zeroed sectors: " + zero;
                }
                else if (r.Sense != null && r.Sense.IsCopyProtection) cssRefused++;
                else otherFail++;
                _log.Info(line);
                if (r.Success) foreach (var l in HexDump.Lines(buf, 0, 48, lba * SectorSize)) _log.Info("    " + l);
            }
            _summary.Add("Samples: " + okCount + "/" + points.Count + " read; refused by CSS: " + cssRefused +
                         "; other failures: " + otherFail + "; MPEG packs " + packs + " (scrambled " + scrambled + ")");
            if (cssRefused > 0)
                _summary.Add("=> The drive REFUSES protected sectors without CSS authentication.");
            else if (scrambled > 0)
                _summary.Add("=> The drive returns the sectors, but SCRAMBLED (CSS). The ISO will need decrypting on the PC.");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>DVD-Video sector: MPEG-2 pack header (00 00 01 BA).</summary>
        private static bool IsMpegPack(byte[] b, int o)
        {
            return b[o] == 0 && b[o + 1] == 0 && b[o + 2] == 1 && b[o + 3] == 0xBA;
        }

        /// <summary>
        /// In VOBs, byte 0x14 of the sector holds the PES_scrambling_control bits (0x30) of the first PES
        /// packet. Non-zero = content scrambled by CSS (the same test libdvdcss uses).
        /// Only valid if the PES packet at 0x0E is video (E0-EF), MPEG audio (C0-DF) or private stream 1 (BD);
        /// in navigation packs (system header 0xBB) byte 0x14 is a different field and CSS never applies.
        /// </summary>
        private static bool IsScrambledPack(byte[] b, int o)
        {
            if (b[o + 0x0E] != 0 || b[o + 0x0F] != 0 || b[o + 0x10] != 1) return false;
            byte stream = b[o + 0x11];
            bool media = (stream >= 0xC0 && stream <= 0xEF) || stream == 0xBD;
            return media && (b[o + 0x14] & 0x30) != 0;
        }

        private void DumpSector(byte[] data, int sector, int bytes)
        {
            if ((sector + 1) * SectorSize > data.Length) return;
            _log.Info("Hex dump of sector " + sector + " (first " + bytes + " bytes):");
            foreach (var l in HexDump.Lines(data, sector * SectorSize, bytes, (long)sector * SectorSize))
                _log.Info("  " + l);
        }

        /// <summary>RMI / RPC mask: bit n SET = region n+1 NOT allowed.</summary>
        private static string RegionList(int mask)
        {
            var allowed = new List<string>();
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) == 0) allowed.Add((i + 1).ToString());
            if (allowed.Count == 8) return "all";
            if (allowed.Count == 0) return "none";
            return string.Join(",", allowed);
        }

        private static string FormatBytes(long bytes)
        {
            return (bytes / 1073741824.0).ToString("0.00") + " GiB";
        }
    }
}
