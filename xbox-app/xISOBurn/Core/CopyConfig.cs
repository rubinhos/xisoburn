using System;
using System.Globalization;
using System.IO;
using System.Text;
using Windows.Data.Json;

namespace xISOBurn.Core
{
    /// <summary>
    /// Settings, stored in LocalState\config.json. If the file does not exist it is created with
    /// the defaults; after every successful load it is rewritten with all current keys (unknown or
    /// obsolete keys are dropped), so the file always documents what the app actually uses.
    /// To edit it without a keyboard on the console, download it from the Device Portal
    /// (File explorer > LocalAppData > xISOBurn_... > LocalState), edit it on the PC and upload it back.
    /// The file is read again before every run.
    /// </summary>
    internal sealed class CopyConfig
    {
        public const string FileName = "config.json";
        public const int MaxSectorsPerRead = 64;

        /// <summary>Sectors per READ(10) in good areas. 16..64 work on the Series X drive.</summary>
        public int SectorsPerRead = 32;
        /// <summary>Maximum time per SCSI command (seconds). On the Xbox drive a failing read took ~6 s.</summary>
        public int TimeoutSeconds = 20;
        /// <summary>
        /// Pass 1: when errors are dense, jump ahead and come back later ("fill" pass), so the easy data
        /// is secured first. false = read strictly in order.
        /// </summary>
        public bool SkipOnErrors = true;
        /// <summary>Extra passes over sectors that are still bad after the scrape pass (1 attempt per sector per pass).</summary>
        public int RetryPasses = 2;
        /// <summary>
        /// Drive read speed (kB/s) for the scrape and retry passes; 0 = do not change. 1385 = DVD 1x.
        /// Reading slowly often recovers scratched sectors. Experimental: the drive may ignore it.
        /// </summary>
        public int RetryReadSpeedKBps = 1385;
        /// <summary>
        /// Drive-internal read retry count (MODE page 01h, byte 3) during pass 1; -1 = do not change.
        /// Lower = each failing read gives up sooner (it took ~6 s with the drive defaults).
        /// Experimental and volatile; the original value is restored at the end of every run.
        /// </summary>
        public int DriveReadRetryCount = -1;
        /// <summary>First sector of the work range (for testing a range). The image always covers the whole disc.</summary>
        public long StartSector = 0;
        /// <summary>Number of sectors in the work range (0 = up to the end of the disc).</summary>
        public long SectorLimit = 0;
        /// <summary>false = only read and build the map, without writing the image (drive test).</summary>
        public bool WriteImage = true;
        /// <summary>File name without extension. Empty = the disc's volume label.</summary>
        public string OutputName = "";
        /// <summary>
        /// CSS-protected DVD-Video: authenticate with the drive and decrypt the video while copying, so the
        /// ISO plays anywhere. false = copy the sectors as the drive returns them (still scrambled).
        /// </summary>
        public bool DecryptCss = true;
        /// <summary>TCP port of the web page served by the app (the PC browser downloads from it). Needs an app restart.</summary>
        public int WebPort = 8770;
        /// <summary>Ask for the PIN on the web page (so nobody else on the network can download or delete copies).</summary>
        public bool WebPinRequired = true;
        /// <summary>4-digit PIN shown on the Xbox screen; generated on first launch.</summary>
        public string WebPin = "";

        public const int DefaultWebPort = 8770;

        public static string NewPin()
        {
            var bytes = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return (BitConverter.ToUInt32(bytes, 0) % 10000).ToString("0000");
        }

        public static CopyConfig Load(string folder, out string message)
        {
            var cfg = new CopyConfig();
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                cfg.Clamp(); // generates the PIN
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
                cfg.TimeoutSeconds = (int)o.GetNamedNumber("TimeoutSeconds", cfg.TimeoutSeconds);
                cfg.SkipOnErrors = o.GetNamedBoolean("SkipOnErrors", cfg.SkipOnErrors);
                cfg.RetryPasses = (int)o.GetNamedNumber("RetryPasses", cfg.RetryPasses);
                cfg.RetryReadSpeedKBps = (int)o.GetNamedNumber("RetryReadSpeedKBps", cfg.RetryReadSpeedKBps);
                cfg.DriveReadRetryCount = (int)o.GetNamedNumber("DriveReadRetryCount", cfg.DriveReadRetryCount);
                cfg.StartSector = (long)o.GetNamedNumber("StartSector", cfg.StartSector);
                cfg.SectorLimit = (long)o.GetNamedNumber("SectorLimit", cfg.SectorLimit);
                cfg.WriteImage = o.GetNamedBoolean("WriteImage", cfg.WriteImage);
                cfg.OutputName = o.GetNamedString("OutputName", cfg.OutputName);
                cfg.DecryptCss = o.GetNamedBoolean("DecryptCss", cfg.DecryptCss);
                cfg.WebPort = (int)o.GetNamedNumber("WebPort", cfg.WebPort);
                cfg.WebPinRequired = o.GetNamedBoolean("WebPinRequired", cfg.WebPinRequired);
                cfg.WebPin = o.GetNamedString("WebPin", cfg.WebPin);
            }
            catch (Exception ex)
            {
                // GetNamed* throws if a value has the wrong type (e.g. a number in quotes).
                message = "config.json has a value of the wrong type (" + ex.Message + "); using the defaults. The file was not changed.";
                return new CopyConfig();
            }
            bool adjusted = cfg.Clamp();
            try { cfg.Save(folder); } catch (Exception) { /* keep going with the values in memory */ }
            message = adjusted ? "config.json loaded (some out-of-range values were adjusted)." : "config.json loaded.";
            return cfg;
        }

        /// <summary>Keeps values within safe ranges. Returns true if anything was adjusted.</summary>
        private bool Clamp()
        {
            int spr = Math.Max(1, Math.Min(MaxSectorsPerRead, SectorsPerRead));
            int to = Math.Max(5, Math.Min(120, TimeoutSeconds));
            int rp = Math.Max(0, Math.Min(100, RetryPasses));
            int rs = RetryReadSpeedKBps <= 0 ? 0 : Math.Max(176, Math.Min(0xFFFF, RetryReadSpeedKBps));
            int rc = DriveReadRetryCount < 0 ? -1 : Math.Min(255, DriveReadRetryCount);
            long ss = Math.Max(0, StartSector);
            long sl = Math.Max(0, SectorLimit);
            // Ports below 1024 are reserved; 8770, 8080 and 50080 are known to work on the Xbox.
            int port = WebPort >= 1024 && WebPort <= 65535 ? WebPort : DefaultWebPort;
            string pin = (WebPin ?? "").Trim();
            bool pinOk = pin.Length >= 4 && pin.Length <= 8;
            foreach (char ch in pin) if (ch < '0' || ch > '9') pinOk = false;
            if (!pinOk) pin = NewPin();
            bool changed = spr != SectorsPerRead || to != TimeoutSeconds || rp != RetryPasses || rs != RetryReadSpeedKBps ||
                           rc != DriveReadRetryCount || ss != StartSector || sl != SectorLimit || port != WebPort;
            SectorsPerRead = spr; TimeoutSeconds = to; RetryPasses = rp; RetryReadSpeedKBps = rs;
            DriveReadRetryCount = rc; StartSector = ss; SectorLimit = sl; WebPort = port; WebPin = pin;
            return changed;
        }

        public void Save(string folder)
        {
            // Written by hand so it stays readable and keeps a fixed order.
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"SectorsPerRead\": ").Append(SectorsPerRead.ToString(inv)).Append(",\n");
            sb.Append("  \"TimeoutSeconds\": ").Append(TimeoutSeconds.ToString(inv)).Append(",\n");
            sb.Append("  \"SkipOnErrors\": ").Append(SkipOnErrors ? "true" : "false").Append(",\n");
            sb.Append("  \"RetryPasses\": ").Append(RetryPasses.ToString(inv)).Append(",\n");
            sb.Append("  \"RetryReadSpeedKBps\": ").Append(RetryReadSpeedKBps.ToString(inv)).Append(",\n");
            sb.Append("  \"DriveReadRetryCount\": ").Append(DriveReadRetryCount.ToString(inv)).Append(",\n");
            sb.Append("  \"StartSector\": ").Append(StartSector.ToString(inv)).Append(",\n");
            sb.Append("  \"SectorLimit\": ").Append(SectorLimit.ToString(inv)).Append(",\n");
            sb.Append("  \"WriteImage\": ").Append(WriteImage ? "true" : "false").Append(",\n");
            sb.Append("  \"OutputName\": ").Append(JsonValue.CreateStringValue(OutputName ?? "").Stringify()).Append(",\n");
            sb.Append("  \"DecryptCss\": ").Append(DecryptCss ? "true" : "false").Append(",\n");
            sb.Append("  \"WebPort\": ").Append(WebPort.ToString(inv)).Append(",\n");
            sb.Append("  \"WebPinRequired\": ").Append(WebPinRequired ? "true" : "false").Append(",\n");
            sb.Append("  \"WebPin\": ").Append(JsonValue.CreateStringValue(WebPin ?? "").Stringify()).Append("\n");
            sb.Append("}\n");
            File.WriteAllText(Path.Combine(folder, FileName), sb.ToString(), new UTF8Encoding(false));
        }

        public string Describe()
        {
            return "block " + SectorsPerRead + " sectors | timeout " + TimeoutSeconds + " s | skip on errors: " + (SkipOnErrors ? "yes" : "no") +
                   " | retry passes " + RetryPasses +
                   " | retry speed " + (RetryReadSpeedKBps > 0 ? RetryReadSpeedKBps + " kB/s" : "unchanged") +
                   " | drive retries " + (DriveReadRetryCount >= 0 ? DriveReadRetryCount.ToString() : "unchanged") +
                   " | range " + StartSector + "+" + (SectorLimit > 0 ? SectorLimit.ToString() : "end") +
                   " | write image: " + (WriteImage ? "yes" : "no") +
                   " | decrypt CSS: " + (DecryptCss ? "yes" : "no") +
                   " | web port " + WebPort + (WebPinRequired ? " with PIN" : " without PIN");
        }
    }
}
