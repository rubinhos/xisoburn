namespace xISOBurn.Scsi
{
    /// <summary>Result of a SCSI command sent through pass-through.</summary>
    internal sealed class ScsiResult
    {
        /// <summary>The IOCTL call (SendIOControlAsync) completed without an exception.</summary>
        public bool IoctlOk;
        /// <summary>HRESULT of the exception, when IoctlOk == false.</summary>
        public int HResult;
        public string IoctlError;

        /// <summary>SCSI status: 0 = GOOD, 2 = CHECK CONDITION (see Sense).</summary>
        public byte ScsiStatus;
        /// <summary>Data bytes actually transferred (returned DataTransferLength).</summary>
        public int DataLength;
        public SenseInfo Sense;
        public double ElapsedMs;

        public bool Success { get { return IoctlOk && ScsiStatus == 0; } }

        public string Describe()
        {
            if (!IoctlOk)
                return "IOCTL failed: HRESULT 0x" + HResult.ToString("X8") + " " + IoctlError +
                       (Sense != null ? ", sense " + Sense : "");
            if (ScsiStatus == 0)
                return "OK (" + DataLength + " bytes, " + ElapsedMs.ToString("0.0") + " ms)";
            return "SCSI status 0x" + ScsiStatus.ToString("X2") +
                   (Sense != null ? ", sense " + Sense : ", no sense") +
                   " (" + ElapsedMs.ToString("0.0") + " ms)";
        }
    }
}
