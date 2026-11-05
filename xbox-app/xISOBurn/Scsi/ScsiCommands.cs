using xISOBurn.Util;

namespace xISOBurn.Scsi
{
    /// <summary>
    /// CDB (Command Descriptor Block) builders. All multi-byte fields are big-endian.
    /// References: SPC-4 (INQUIRY, TEST UNIT READY, REQUEST SENSE), SBC/MMC-6 (READ CAPACITY,
    /// READ(10), GET CONFIGURATION, READ DVD STRUCTURE, REPORT KEY, READ TOC).
    /// </summary>
    internal static class ScsiCommands
    {
        /// <summary>TEST UNIT READY (0x00): no data; GOOD = disc ready.</summary>
        public static byte[] TestUnitReady()
        {
            return new byte[6];
        }

        /// <summary>REQUEST SENSE (0x03): asks for the sense of the last error when the driver did not return it.</summary>
        public static byte[] RequestSense(byte allocationLength)
        {
            return new byte[] { 0x03, 0, 0, 0, allocationLength, 0 };
        }

        /// <summary>INQUIRY (0x12): drive vendor, model and firmware.</summary>
        public static byte[] Inquiry(byte allocationLength)
        {
            return new byte[] { 0x12, 0, 0, 0, allocationLength, 0 };
        }

        /// <summary>
        /// READ CAPACITY(10) (0x25): returns the LAST LBA (not the count) and the block size.
        /// Sectors = last LBA + 1. Warning: on the Xbox drive (PLDS DG-6M5S) this was WRONG for an OTP
        /// dual-layer DVD ("end - start + 1" of the physical sector numbers); see docs/spike.md.
        /// </summary>
        public static byte[] ReadCapacity10()
        {
            return new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        }

        /// <summary>
        /// GET CONFIGURATION (0x46), RT=0, starting at feature 0. Bytes 6-7 of the header hold the
        /// "current profile", which identifies the media (CD-ROM, DVD-ROM, BD-ROM...).
        /// </summary>
        public static byte[] GetConfiguration(int allocationLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x46;
            Bytes.PutBE16(cdb, 7, allocationLength);
            return cdb;
        }

        /// <summary>
        /// READ(10) (0x28): reads 'count' 2048-byte sectors starting at 'lba'.
        /// Same CDB as XBSX2 (DPO/FUA = 0: the drive may use its own cache).
        /// </summary>
        public static byte[] Read10(uint lba, int count)
        {
            var cdb = new byte[10];
            cdb[0] = 0x28;
            Bytes.PutBE32(cdb, 2, lba);
            Bytes.PutBE16(cdb, 7, count);
            return cdb;
        }

        /// <summary>
        /// READ DVD STRUCTURE (0xAD), media type 0 (DVD).
        ///   format 0x00 = Physical Format Information (book type, layers, sectors);
        ///   format 0x01 = Copyright Information (protection system and disc regions).
        /// Format 0x02 (Disc Key) requires CSS authentication and is NOT used.
        /// </summary>
        public static byte[] ReadDvdStructure(byte layer, byte format, int allocationLength)
        {
            var cdb = new byte[12];
            cdb[0] = 0xAD;
            cdb[6] = layer;
            cdb[7] = format;
            Bytes.PutBE16(cdb, 8, allocationLength);
            return cdb;
        }

        /// <summary>
        /// REPORT KEY (0xA4) with KEY FORMAT 0x08 = "RPC State": only reports the region set in the
        /// drive and how many changes remain. Takes no part in CSS authentication and changes nothing.
        /// </summary>
        public static byte[] ReportKeyRpcState()
        {
            var cdb = new byte[12];
            cdb[0] = 0xA4;
            Bytes.PutBE16(cdb, 8, 8); // allocation length
            cdb[10] = 0x08;            // AGID 0 (bits 7-6) | KEY FORMAT 0x08
            return cdb;
        }

        /// <summary>
        /// SET CD SPEED (0xBB): read speed in kB/s (1000 bytes/s), 0xFFFF = maximum. MMC defines it for
        /// CDs, but most drives also apply it to DVDs (DVD 1x = 1385 kB/s). XBSX2 sends this same CDB.
        /// Reading a scratched disc more slowly often recovers sectors that fail at full speed.
        /// The setting is volatile: the drive returns to its default after a reset or disc change.
        /// </summary>
        public static byte[] SetCdSpeed(int readKBps)
        {
            var cdb = new byte[12];
            cdb[0] = 0xBB;
            Bytes.PutBE16(cdb, 2, readKBps);
            Bytes.PutBE16(cdb, 4, 0xFFFF); // write speed: leave at maximum
            return cdb;
        }

        /// <summary>MODE SENSE(10) (0x5A) of one page, current values, without block descriptors (DBD=1).</summary>
        public static byte[] ModeSense10(byte page, int allocationLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x5A;
            cdb[1] = 0x08;              // DBD
            cdb[2] = (byte)(page & 0x3F); // PC = 00 (current values)
            Bytes.PutBE16(cdb, 7, allocationLength);
            return cdb;
        }

        /// <summary>
        /// MODE SELECT(10) (0x55) with PF=1, SP=0: the change is NOT saved in the drive (volatile), so a
        /// power cycle always restores the original behaviour.
        /// </summary>
        public static byte[] ModeSelect10(int parameterListLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x55;
            cdb[1] = 0x10;              // PF
            Bytes.PutBE16(cdb, 7, parameterListLength);
            return cdb;
        }

        /// <summary>READ TOC/PMA/ATIP (0x43), format 0 (TOC), LBA addresses. CDs only.</summary>
        public static byte[] ReadToc(int allocationLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x43;
            Bytes.PutBE16(cdb, 7, allocationLength);
            return cdb;
        }
    }
}
