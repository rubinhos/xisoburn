using System.Text;

namespace xISOBurn.Core
{
    /// <summary>
    /// The checksums of a finished image, the ones redump.org lists for a disc (size, CRC32, MD5,
    /// SHA-1), plus SHA-256. Written next to the image as NAME.hashes.
    /// </summary>
    internal sealed class ImageHashes
    {
        public const string Extension = ".hashes";

        public long Size;
        public string Crc32 = "", Md5 = "", Sha1 = "", Sha256 = "";

        public string ToText(string imageName)
        {
            var sb = new StringBuilder();
            sb.Append("File: ").Append(imageName).Append('\n');
            sb.Append("Size: ").Append(Size.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" bytes\n");
            sb.Append("CRC32: ").Append(Crc32).Append('\n');
            sb.Append("MD5: ").Append(Md5).Append('\n');
            sb.Append("SHA-1: ").Append(Sha1).Append('\n');
            sb.Append("SHA-256: ").Append(Sha256).Append('\n');
            return sb.ToString();
        }

        /// <summary>One line for the log and the status text.</summary>
        public string Describe()
        {
            return "CRC32 " + Crc32 + " | MD5 " + Md5 + " | SHA-1 " + Sha1 + " | SHA-256 " + Sha256;
        }
    }
}
