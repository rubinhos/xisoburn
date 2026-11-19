namespace xISOBurn.Util
{
    /// <summary>
    /// CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320), the one Redump, zip and sfv use.
    /// Incremental: Append the data in pieces, then read Value. (.NET Standard 2.0 has no CRC-32.)
    /// </summary>
    internal sealed class Crc32
    {
        private static readonly uint[] Table = MakeTable();
        private uint _crc = 0xFFFFFFFFu;

        private static uint[] MakeTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        public void Append(byte[] data, int offset, int count)
        {
            uint crc = _crc;
            int end = offset + count;
            for (int i = offset; i < end; i++)
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            _crc = crc;
        }

        public uint Value { get { return _crc ^ 0xFFFFFFFFu; } }
    }
}
