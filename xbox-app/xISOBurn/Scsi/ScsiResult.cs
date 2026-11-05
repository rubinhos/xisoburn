namespace xISOBurn.Scsi
{
    /// <summary>Resultado de um comando SCSI enviado por pass-through.</summary>
    internal sealed class ScsiResult
    {
        /// <summary>A chamada IOCTL (SendIOControlAsync) completou sem exceção.</summary>
        public bool IoctlOk;
        /// <summary>HRESULT da exceção, quando IoctlOk == false.</summary>
        public int HResult;
        public string IoctlError;

        /// <summary>Status SCSI: 0 = GOOD, 2 = CHECK CONDITION (ver Sense).</summary>
        public byte ScsiStatus;
        /// <summary>Bytes de dados efetivamente transferidos (DataTransferLength devolvido).</summary>
        public int DataLength;
        public SenseInfo Sense;
        public double ElapsedMs;

        public bool Success { get { return IoctlOk && ScsiStatus == 0; } }

        public string Describe()
        {
            if (!IoctlOk)
                return "falha no IOCTL: HRESULT 0x" + HResult.ToString("X8") + " " + IoctlError;
            if (ScsiStatus == 0)
                return "OK (" + DataLength + " bytes, " + ElapsedMs.ToString("0.0") + " ms)";
            return "status SCSI 0x" + ScsiStatus.ToString("X2") +
                   (Sense != null ? ", sense " + Sense : ", sem sense") +
                   " (" + ElapsedMs.ToString("0.0") + " ms)";
        }
    }
}
