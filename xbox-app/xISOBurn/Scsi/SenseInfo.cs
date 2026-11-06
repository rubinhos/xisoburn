namespace xISOBurn.Scsi
{
    /// <summary>
    /// "Sense" data returned by the drive when a command ends with CHECK CONDITION.
    /// Key/ASC/ASCQ follow SPC-4 and MMC-6. The table below covers what matters for reading discs
    /// (medium errors, no disc, CSS/region protection); anything else is shown in hex only.
    /// </summary>
    internal sealed class SenseInfo
    {
        public byte Key;
        public byte Asc;
        public byte Ascq;

        /// <summary>Obtained with REQUEST SENSE after the failure (the driver did not return it).</summary>
        public bool FromRequestSense;

        public string Code { get { return Key.ToString("X2") + "/" + Asc.ToString("X2") + "/" + Ascq.ToString("X2"); } }

        /// <summary>02/3A/xx: no disc in the drive. Not a bad sector; the copy has to stop.</summary>
        public bool IsNoMedium { get { return Key == 0x02 && Asc == 0x3A; } }

        /// <summary>05/6F/xx: CSS authentication / region problems (not a physical defect of the disc).</summary>
        public bool IsCopyProtection { get { return Key == 0x05 && Asc == 0x6F; } }

        public static SenseInfo Parse(byte[] sense, int length)
        {
            if (sense == null || length < 3) return null;
            int responseCode = sense[0] & 0x7F;
            var s = new SenseInfo();
            if (responseCode == 0x70 || responseCode == 0x71)
            {
                // Fixed format: key in byte 2, ASC/ASCQ in bytes 12/13.
                s.Key = (byte)(sense[2] & 0x0F);
                s.Asc = length > 12 ? sense[12] : (byte)0;
                s.Ascq = length > 13 ? sense[13] : (byte)0;
            }
            else if (responseCode == 0x72 || responseCode == 0x73)
            {
                // Descriptor format: key/ASC/ASCQ in bytes 1/2/3.
                s.Key = (byte)(sense[1] & 0x0F);
                s.Asc = sense[2];
                s.Ascq = length > 3 ? sense[3] : (byte)0;
            }
            else
            {
                return null;
            }
            return s;
        }

        public override string ToString()
        {
            return Code + " " + Describe() + (FromRequestSense ? " [via REQUEST SENSE]" : "");
        }

        public string Describe()
        {
            switch ((Asc << 8) | Ascq)
            {
                case 0x0401: return "NOT READY: BECOMING READY (disc spinning up)";
                case 0x0402: return "NOT READY: INITIALIZING COMMAND REQUIRED";
                case 0x3A00: return "MEDIUM NOT PRESENT (no disc)";
                case 0x3A01: return "MEDIUM NOT PRESENT - TRAY CLOSED";
                case 0x3A02: return "MEDIUM NOT PRESENT - TRAY OPEN";
                case 0x1100: return "UNRECOVERED READ ERROR";
                case 0x1105: return "L-EC UNCORRECTABLE ERROR";
                case 0x1106: return "CIRC UNRECOVERED ERROR";
                case 0x1000: return "ID CRC OR ECC ERROR";
                case 0x0200: return "NO SEEK COMPLETE";
                case 0x1500: return "RANDOM POSITIONING ERROR";
                case 0x1502: return "POSITIONING ERROR DETECTED BY READ OF MEDIUM";
                case 0x3100: return "MEDIUM FORMAT CORRUPTED";
                case 0x3000: return "INCOMPATIBLE MEDIUM INSTALLED";
                case 0x3002: return "CANNOT READ MEDIUM - INCOMPATIBLE FORMAT";
                case 0x2000: return "INVALID COMMAND OPERATION CODE";
                case 0x2100: return "LOGICAL BLOCK ADDRESS OUT OF RANGE";
                case 0x2400: return "INVALID FIELD IN CDB";
                case 0x6300: return "END OF USER AREA ENCOUNTERED ON THIS TRACK";
                case 0x6400: return "ILLEGAL MODE FOR THIS TRACK";
                case 0x6F00: return "COPY PROTECTION KEY EXCHANGE FAILURE - AUTHENTICATION FAILURE";
                case 0x6F01: return "COPY PROTECTION KEY EXCHANGE FAILURE - KEY NOT PRESENT";
                case 0x6F02: return "COPY PROTECTION KEY EXCHANGE FAILURE - KEY NOT ESTABLISHED";
                case 0x6F03: return "READ OF SCRAMBLED SECTOR WITHOUT AUTHENTICATION (CSS)";
                case 0x6F04: return "MEDIA REGION CODE IS MISMATCHED TO LOGICAL UNIT REGION";
                case 0x6F05: return "DRIVE REGION MUST BE PERMANENT/REGION RESET COUNT ERROR";
                case 0x2800: return "UNIT ATTENTION: NOT READY TO READY CHANGE (disc changed?)";
                case 0x2900: return "UNIT ATTENTION: POWER ON / RESET";
                case 0x5A01: return "OPERATOR MEDIUM REMOVAL REQUEST";
            }
            switch (Key)
            {
                case 0x0: return "NO SENSE";
                case 0x1: return "RECOVERED ERROR";
                case 0x2: return "NOT READY";
                case 0x3: return "MEDIUM ERROR";
                case 0x4: return "HARDWARE ERROR";
                case 0x5: return "ILLEGAL REQUEST";
                case 0x6: return "UNIT ATTENTION";
                case 0xB: return "ABORTED COMMAND";
                default: return "key " + Key.ToString("X");
            }
        }
    }
}
