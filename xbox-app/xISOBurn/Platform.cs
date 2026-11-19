namespace xISOBurn
{
    /// <summary>The words the shared engine (engine/) uses for things that differ between the apps.</summary>
    internal static class Platform
    {
        /// <summary>Where Blu-ray key files are added (AACS log, no KEYDB.cfg).</summary>
        public const string AddKeysHint = "Upload them on the web page (Blu-ray keys)";
        /// <summary>How to get the drive out of a bad state (disc scan).</summary>
        public const string ResetDriveHint = "power the console off fully";
        /// <summary>What to do when the destination is full.</summary>
        public const string OtherDestinationHint = "Choose another destination in Settings (a USB drive)";
    }
}
