using System.Text;

namespace xISOBurn.Util
{
    /// <summary>
    /// Integer read/write helpers. SCSI uses big-endian in CDBs and responses;
    /// the Windows SCSI_PASS_THROUGH structure uses little-endian (x64).
    /// </summary>
    internal static class Bytes
    {
        public static int BE16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        public static uint BE24(byte[] b, int o) { return ((uint)b[o] << 16) | ((uint)b[o + 1] << 8) | b[o + 2]; }
        public static uint BE32(byte[] b, int o) { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }

        public static ushort LE16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        public static uint LE32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }

        public static void PutLE16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        public static void PutLE32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        public static void PutLE64(byte[] b, int o, ulong v)
        {
            PutLE32(b, o, (uint)v);
            PutLE32(b, o + 4, (uint)(v >> 32));
        }

        public static void PutBE16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        public static void PutBE32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        /// <summary>Printable ASCII text (other bytes become '.'), trimmed.</summary>
        public static string Ascii(byte[] b, int o, int n)
        {
            var sb = new StringBuilder(n);
            for (int i = 0; i < n && o + i < b.Length; i++)
            {
                byte c = b[o + i];
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
            }
            return sb.ToString().Trim();
        }

        public static bool AllZero(byte[] b, int o, int n)
        {
            for (int i = 0; i < n; i++)
                if (b[o + i] != 0) return false;
            return true;
        }

        public static bool Equal(byte[] a, int ao, byte[] b, int bo, int n)
        {
            for (int i = 0; i < n; i++)
                if (a[ao + i] != b[bo + i]) return false;
            return true;
        }
    }
}
