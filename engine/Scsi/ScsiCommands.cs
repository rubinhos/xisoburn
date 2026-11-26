using xISOBurn.Util;

namespace xISOBurn.Scsi
{
    /// <summary>
    /// CDB (Command Descriptor Block) builders. All multi-byte fields are big-endian.
    /// References: SPC-4 (TEST UNIT READY, REQUEST SENSE, MODE SENSE/SELECT), SBC/MMC-6 (READ CAPACITY,
    /// READ(10), GET CONFIGURATION, READ DVD STRUCTURE, SET CD SPEED). The CSS commands (REPORT KEY,
    /// SEND KEY, READ DVD STRUCTURE disc key) are in Css/CssDrive.cs.
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

        /// <summary>
        /// READ CAPACITY(10) (0x25): returns the LAST LBA (not the count) and the block size.
        /// Sectors = last LBA + 1. Warning: on the Xbox drive (PLDS DG-6M5S) this was WRONG for an OTP
        /// dual-layer DVD ("end - start + 1" of the physical sector numbers); see docs/how-it-works.md.
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
        /// Format 0x02 (Disc Key) needs CSS authentication; see Css/CssDrive.cs.
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

        /// <summary>Bytes per sector of a raw CD read: sync, header, user data and EDC/ECC (or 2352 bytes of audio).</summary>
        public const int CdRawSectorSize = 2352;

        /// <summary>
        /// READ CD (0xBE): 'count' whole 2352-byte sectors from 'lba', any sector type (audio or data),
        /// with sync, headers, user data and EDC/ECC (byte 9 = 0xF8) and no subchannel.
        /// </summary>
        public static byte[] ReadCd(uint lba, int count)
        {
            var cdb = new byte[12];
            cdb[0] = 0xBE;
            cdb[1] = 0x00;              // expected sector type: any
            Bytes.PutBE32(cdb, 2, lba);
            cdb[6] = (byte)(count >> 16);
            cdb[7] = (byte)(count >> 8);
            cdb[8] = (byte)count;
            cdb[9] = 0xF8;              // sync + all headers + user data + EDC/ECC
            cdb[10] = 0x00;             // no subchannel
            return cdb;
        }

        /// <summary>
        /// READ TOC/PMA/ATIP (0x43) with LBA addresses: format 0 = the TOC (one 8-byte descriptor per
        /// track, then the lead-out, track 0xAA); format 1 = session information.
        /// </summary>
        public static byte[] ReadToc(byte format, byte track, int allocationLength)
        {
            var cdb = new byte[10];
            cdb[0] = 0x43;
            cdb[2] = (byte)(format & 0x0F);
            cdb[6] = track;
            Bytes.PutBE16(cdb, 7, allocationLength);
            return cdb;
        }

        /// <summary>PREVENT ALLOW MEDIUM REMOVAL (0x1E): prevent = false lets the disc out again.</summary>
        public static byte[] PreventAllowMediumRemoval(bool prevent)
        {
            var cdb = new byte[6];
            cdb[0] = 0x1E;
            cdb[4] = (byte)(prevent ? 1 : 0);
            return cdb;
        }

        /// <summary>START STOP UNIT (0x1B) with LoEj = 1, Start = 0: stops the disc and ejects it.</summary>
        public static byte[] Eject()
        {
            var cdb = new byte[6];
            cdb[0] = 0x1B;
            cdb[4] = 0x02;              // LoEj
            return cdb;
        }
    }
}
