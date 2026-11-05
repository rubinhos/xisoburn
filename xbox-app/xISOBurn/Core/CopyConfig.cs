using System;
using System.Globalization;
using System.IO;
using System.Text;
using Windows.Data.Json;

namespace xISOBurn.Core
{
    /// <summary>
    /// Copy settings, stored in LocalState\config.json. If the file does not exist it is created with
    /// the defaults. To edit it without a keyboard on the console, download it from the Device Portal
    /// (File explorer > LocalAppData > xISOBurn_... > LocalState), edit it on the PC and upload it back.
    /// The file is read again before every copy.
    /// </summary>
    internal sealed class CopyConfig
    {
        public const string FileName = "config.json";

        /// <summary>Sectors per READ(10). The spike showed 16..64 work on the Series X drive.</summary>
        public int SectorsPerRead = 32;
        /// <summary>Extra attempts for the whole block after the first failure.</summary>
        public int BlockRetries = 5;
        /// <summary>Extra attempts for each sector during the sector-by-sector pass.</summary>
        public int SectorRetries = 5;
        /// <summary>Maximum time per SCSI command (seconds). A bad sector can take several seconds.</summary>
        public int TimeoutSeconds = 20;
        /// <summary>First sector to copy (for testing a range).</summary>
        public long StartSector = 0;
        /// <summary>Maximum number of sectors (0 = up to the end of the disc).</summary>
        public long SectorLimit = 0;
        /// <summary>false = only read, hash and build the map, without writing the image (drive test).</summary>
        public bool WriteImage = true;
        /// <summary>File name without extension. Empty = the disc's volume label.</summary>
        public string OutputName = "";

        public const int MaxSectorsPerRead = 64;

        public static CopyConfig Load(string folder, out string message)
        {
            var cfg = new CopyConfig();
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                cfg.Save(folder);
                message = "config.json created with the default values.";
                return cfg;
            }

            JsonObject o;
            if (!JsonObject.TryParse(File.ReadAllText(path), out o))
            {
                message = "config.json is invalid (malformed JSON); using the defaults. The file was not changed.";
                return cfg;
            }

            try
            {
                cfg.SectorsPerRead = (int)o.GetNamedNumber("SectorsPerRead", cfg.SectorsPerRead);
                cfg.BlockRetries = (int)o.GetNamedNumber("BlockRetries", cfg.BlockRetries);
                cfg.SectorRetries = (int)o.GetNamedNumber("SectorRetries", cfg.SectorRetries);
                cfg.TimeoutSeconds = (int)o.GetNamedNumber("TimeoutSeconds", cfg.TimeoutSeconds);
                cfg.StartSector = (long)o.GetNamedNumber("StartSector", cfg.StartSector);
                cfg.SectorLimit = (long)o.GetNamedNumber("SectorLimit", cfg.SectorLimit);
                cfg.WriteImage = o.GetNamedBoolean("WriteImage", cfg.WriteImage);
                cfg.OutputName = o.GetNamedString("OutputName", cfg.OutputName);
            }
            catch (Exception ex)
            {
                // GetNamed* throws if a value has the wrong type (e.g. a number in quotes).
                message = "config.json has a value of the wrong type (" + ex.Message + "); using the defaults.";
                return new CopyConfig();
            }
            message = cfg.Clamp() ? "config.json loaded (some out-of-range values were adjusted)." : "config.json loaded.";
            return cfg;
        }

        /// <summary>Keeps values within safe ranges. Returns true if anything was adjusted.</summary>
        private bool Clamp()
        {
            int spr = Math.Max(1, Math.Min(MaxSectorsPerRead, SectorsPerRead));
            int br = Math.Max(0, Math.Min(50, BlockRetries));
            int sr = Math.Max(0, Math.Min(50, SectorRetries));
            int to = Math.Max(5, Math.Min(120, TimeoutSeconds));
            long ss = Math.Max(0, StartSector);
            long sl = Math.Max(0, SectorLimit);
            bool changed = spr != SectorsPerRead || br != BlockRetries || sr != SectorRetries || to != TimeoutSeconds || ss != StartSector || sl != SectorLimit;
            SectorsPerRead = spr; BlockRetries = br; SectorRetries = sr; TimeoutSeconds = to; StartSector = ss; SectorLimit = sl;
            return changed;
        }

        public void Save(string folder)
        {
            // Written by hand so it stays readable and keeps a fixed order.
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"SectorsPerRead\": ").Append(SectorsPerRead).Append(",\n");
            sb.Append("  \"BlockRetries\": ").Append(BlockRetries).Append(",\n");
            sb.Append("  \"SectorRetries\": ").Append(SectorRetries).Append(",\n");
            sb.Append("  \"TimeoutSeconds\": ").Append(TimeoutSeconds).Append(",\n");
            sb.Append("  \"StartSector\": ").Append(StartSector.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"SectorLimit\": ").Append(SectorLimit.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"WriteImage\": ").Append(WriteImage ? "true" : "false").Append(",\n");
            sb.Append("  \"OutputName\": ").Append(JsonValue.CreateStringValue(OutputName ?? "").Stringify()).Append("\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(folder, FileName), sb.ToString(), new UTF8Encoding(false));
        }

        public string Describe()
        {
            return "block " + SectorsPerRead + " sectors | retries: block " + BlockRetries + ", sector " + SectorRetries +
                   " | timeout " + TimeoutSeconds + " s | start " + StartSector +
                   " | limit " + (SectorLimit > 0 ? SectorLimit.ToString() : "end of disc") +
                   " | write image: " + (WriteImage ? "yes" : "no");
        }
    }
}
