using System.Collections.Generic;
using System.Text;

namespace xISOBurn.Util
{
    internal static class HexDump
    {
        /// <summary>Linhas no formato "00000000  xx xx ... xx  |ascii...........|".</summary>
        public static IEnumerable<string> Lines(byte[] data, int offset, int length, long baseAddress)
        {
            for (int row = 0; row < length; row += 16)
            {
                var sb = new StringBuilder(80);
                sb.Append((baseAddress + row).ToString("X8")).Append("  ");
                int n = System.Math.Min(16, length - row);
                for (int i = 0; i < 16; i++)
                {
                    if (i < n) sb.Append(data[offset + row + i].ToString("x2")).Append(' ');
                    else sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }
                sb.Append(" |");
                for (int i = 0; i < n; i++)
                {
                    byte c = data[offset + row + i];
                    sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
                }
                sb.Append('|');
                yield return sb.ToString();
            }
        }
    }
}
