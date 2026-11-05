using xISOBurn.Util;

namespace xISOBurn.Scsi
{
    /// <summary>MMC profile names (GET CONFIGURATION, "current profile").</summary>
    internal static class MmcProfiles
    {
        public static string Name(int profile)
        {
            switch (profile)
            {
                case 0x0000: return "none (no disc?)";
                case 0x0008: return "CD-ROM";
                case 0x0009: return "CD-R";
                case 0x000A: return "CD-RW";
                case 0x0010: return "DVD-ROM";
                case 0x0011: return "DVD-R";
                case 0x0012: return "DVD-RAM";
                case 0x0013: return "DVD-RW (restricted overwrite)";
                case 0x0014: return "DVD-RW (sequential)";
                case 0x0015: return "DVD-R DL (sequential)";
                case 0x0016: return "DVD-R DL (jump)";
                case 0x001A: return "DVD+RW";
                case 0x001B: return "DVD+R";
                case 0x002A: return "DVD+RW DL";
                case 0x002B: return "DVD+R DL";
                case 0x0040: return "BD-ROM";
                case 0x0041: return "BD-R SRM";
                case 0x0042: return "BD-R RRM";
                case 0x0043: return "BD-RE";
                case 0x0050: return "HD DVD-ROM";
                default: return "unknown";
            }
        }

        public static bool IsCd(int p) { return p >= 0x0008 && p <= 0x000A; }
        public static bool IsDvd(int p) { return p >= 0x0010 && p <= 0x002B; }
        public static bool IsBd(int p) { return p >= 0x0040 && p <= 0x0043; }
    }

    /// <summary>
    /// DVD Physical Format Information (READ DVD STRUCTURE format 0), per MMC-6 / ECMA-267.
    /// The descriptor starts at byte 4 of the response (after the 4-byte header).
    /// Addresses here are PHYSICAL sector numbers (DVD-ROM starts at 0x30000), not LBAs.
    /// </summary>
    internal sealed class DvdPhysicalFormat
    {
        public int BookType, PartVersion, DiscSize, MaxRate;
        public int NumberOfLayers;     // 1 or 2
        public bool OppositeTrackPath; // true = OTP (common on DL movies), false = PTP
        public int LayerType;          // bit0 embossed (ROM), bit1 recordable, bit2 rewritable
        public uint StartSector, EndSector, EndSectorLayer0;
        public bool Bca;

        public static DvdPhysicalFormat Parse(byte[] r, int length)
        {
            if (length < 4 + 17) return null;
            const int d = 4;
            var p = new DvdPhysicalFormat();
            p.BookType = r[d] >> 4;
            p.PartVersion = r[d] & 0x0F;
            p.DiscSize = r[d + 1] >> 4;
            p.MaxRate = r[d + 1] & 0x0F;
            p.NumberOfLayers = ((r[d + 2] >> 5) & 0x03) + 1; // the field stores "layers - 1"
            p.OppositeTrackPath = ((r[d + 2] >> 4) & 1) == 1;
            p.LayerType = r[d + 2] & 0x0F;
            // Sector fields are 4 bytes wide but only 24 bits are valid (XBSX2 masks them too).
            p.StartSector = Bytes.BE32(r, d + 4) & 0xFFFFFF;
            p.EndSector = Bytes.BE32(r, d + 8) & 0xFFFFFF;
            p.EndSectorLayer0 = Bytes.BE32(r, d + 12) & 0xFFFFFF;
            p.Bca = (r[d + 16] & 0x80) != 0;
            return p;
        }

        /// <summary>Sectors in layer 0 (for OTP); this is where the layer change happens in LBA terms.</summary>
        public long Layer0Sectors
        {
            get
            {
                if (NumberOfLayers > 1 && OppositeTrackPath) return (long)EndSectorLayer0 - StartSector + 1;
                return (long)EndSector - StartSector + 1;
            }
        }

        /// <summary>
        /// Total data sectors according to this structure.
        /// OTP: layer 1 uses sector numbers that are the bitwise complement of layer 0 at the same radius.
        /// It starts at ~EndSectorLayer0 and ends at EndSector. Therefore:
        ///   total = (EndL0 - Start + 1) + (End - (~EndL0 &amp; 0xFFFFFF) + 1)
        /// PTP dual layer: each layer is described separately (add layer 0 and layer 1).
        /// </summary>
        public long TotalSectors
        {
            get
            {
                if (NumberOfLayers > 1 && OppositeTrackPath)
                {
                    long l1Start = (~EndSectorLayer0) & 0xFFFFFF;
                    return Layer0Sectors + ((long)EndSector - l1Start + 1);
                }
                return (long)EndSector - StartSector + 1;
            }
        }

        public string BookTypeName
        {
            get
            {
                switch (BookType)
                {
                    case 0: return "DVD-ROM";
                    case 1: return "DVD-RAM";
                    case 2: return "DVD-R";
                    case 3: return "DVD-RW";
                    case 9: return "DVD+RW";
                    case 10: return "DVD+R";
                    case 13: return "DVD+RW DL";
                    case 14: return "DVD+R DL";
                    default: return "type " + BookType;
                }
            }
        }

        public string MaxRateName
        {
            get
            {
                switch (MaxRate)
                {
                    case 0: return "2.52 Mbps";
                    case 1: return "5.04 Mbps";
                    case 2: return "10.08 Mbps";
                    case 3: return "20.16 Mbps";
                    case 4: return "30.24 Mbps";
                    case 15: return "not specified";
                    default: return "code " + MaxRate;
                }
            }
        }
    }
}
