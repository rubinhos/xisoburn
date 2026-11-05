using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Custom;
using Windows.Devices.Enumeration;
using xISOBurn.Util;

namespace xISOBurn.Scsi
{
    /// <summary>
    /// Acesso ao drive óptico do Xbox por Windows.Devices.Custom + SCSI pass-through.
    ///
    /// Por que assim: em apps de console, CreateFile("\\.\X:") e os IOCTL_CDROM/IOCTL_DVD são
    /// recusados; o caminho que abre em Modo Dev é a interface de dispositivo CD-ROM aberta como
    /// CustomDevice. Isto replica pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp do XBSX2 (PR #11):
    ///   - DeviceInformation.FindAllAsync com o filtro AQS da classe de interface CD-ROM;
    ///   - CustomDevice.FromIdAsync(id, ReadWrite, Shared);
    ///   - IOControlCode(IOCTL_SCSI_BASE=0x0004, função 0x401, ReadWrite, Buffered)
    ///     = IOCTL_SCSI_PASS_THROUGH (0x4D004), a variante COM BUFFER (não a _DIRECT): a estrutura,
    ///     o sense e os dados vão num único buffer contíguo.
    /// </summary>
    internal sealed class OpticalDrive : IDisposable
    {
        /// <summary>GUID_DEVINTERFACE_CDROM (o mesmo declarado como DeviceCapability no manifesto).</summary>
        public const string CdromInterfaceClass = "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}";

        /// <summary>Filtro AQS idêntico ao DRIVE_SELECTOR do XBSX2.</summary>
        public const string Selector = "System.Devices.InterfaceClassGuid:=\"" + CdromInterfaceClass + "\"";

        public const int SectorSize = 2048;

        // Layout de SCSI_PASS_THROUGH em x64 (ntddscsi.h), sizeof = 56:
        //   0 USHORT Length          2 UCHAR ScsiStatus      3 UCHAR PathId
        //   4 UCHAR TargetId         5 UCHAR Lun             6 UCHAR CdbLength
        //   7 UCHAR SenseInfoLength  8 UCHAR DataIn          (9..11 alinhamento)
        //  12 ULONG DataTransferLength                       16 ULONG TimeOutValue
        //  (20..23 alinhamento)     24 ULONG_PTR DataBufferOffset (8 bytes em x64)
        //  32 ULONG SenseInfoOffset 36 UCHAR Cdb[16]         (52..55 alinhamento)
        // O app é só x64; em processo 32 bits o layout seria outro (SCSI_PASS_THROUGH32).
        private const int SptSize = 56;
        private const int OffLength = 0, OffScsiStatus = 2, OffCdbLength = 6, OffSenseLength = 7, OffDataIn = 8;
        private const int OffTransferLength = 12, OffTimeout = 16, OffDataOffset = 24, OffSenseOffset = 32, OffCdb = 36;
        private const int SenseSize = 32;
        /// <summary>Cabeçalho = estrutura + sense; os dados começam logo depois (como no XBSX2).</summary>
        public const int HeaderSize = SptSize + SenseSize; // 88

        private const byte SCSI_IOCTL_DATA_IN = 1;
        private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

        private readonly CustomDevice _device;
        private readonly IOControlCode _code;
        private readonly byte[] _in;
        private readonly byte[] _out;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        /// <summary>Maior transferência de dados aceita por este objeto (tamanho dos buffers).</summary>
        public int MaxTransferBytes { get; private set; }
        public string DeviceId { get; private set; }
        public uint ControlCode { get { return _code.ControlCode; } }

        private OpticalDrive(CustomDevice device, string id, int maxTransferBytes)
        {
            _device = device;
            DeviceId = id;
            MaxTransferBytes = maxTransferBytes;
            _code = new IOControlCode(0x0004 /* IOCTL_SCSI_BASE = FILE_DEVICE_CONTROLLER */, 0x401,
                IOControlAccessMode.ReadWrite, IOControlBufferingMethod.Buffered);
            _in = new byte[HeaderSize + maxTransferBytes];
            _out = new byte[HeaderSize + maxTransferBytes];
        }

        public static async Task<IReadOnlyList<DeviceInformation>> FindAsync()
        {
            var props = new[] { "System.Devices.InterfaceEnabled" };
            var found = await DeviceInformation.FindAllAsync(Selector, props);
            var list = new List<DeviceInformation>();
            foreach (var d in found) list.Add(d);
            return list;
        }

        /// <summary>Abre o drive. Lança exceção (com HRESULT) se o sistema recusar.</summary>
        public static async Task<OpticalDrive> OpenAsync(string deviceId, int maxTransferBytes)
        {
            var device = await CustomDevice.FromIdAsync(deviceId, DeviceAccessMode.ReadWrite, DeviceSharingMode.Shared);
            if (device == null)
                // O XBSX2 trata "null sem exceção" como acesso negado (capacidade ausente/consentimento).
                throw new UnauthorizedAccessException("CustomDevice.FromIdAsync devolveu null (acesso negado?)");
            return new OpticalDrive(device, deviceId, maxTransferBytes);
        }

        /// <summary>
        /// Envia um CDB. Se dataLength > 0, é um comando de leitura (DATA IN) e os dados são copiados
        /// para dest[destOffset..]. Nunca lança exceção: erros voltam no ScsiResult.
        /// </summary>
        public async Task<ScsiResult> SendAsync(byte[] cdb, int dataLength, byte[] dest, int destOffset, uint timeoutSeconds)
        {
            if (cdb.Length == 0 || cdb.Length > 16) throw new ArgumentException("CDB deve ter 1..16 bytes");
            if (dataLength < 0 || dataLength > MaxTransferBytes) throw new ArgumentOutOfRangeException("dataLength");

            await _gate.WaitAsync();
            var sw = Stopwatch.StartNew();
            var result = new ScsiResult();
            try
            {
                Array.Clear(_in, 0, HeaderSize);
                Array.Clear(_out, 0, HeaderSize);
                Bytes.PutLE16(_in, OffLength, SptSize);
                _in[OffCdbLength] = (byte)cdb.Length;
                _in[OffSenseLength] = SenseSize;
                _in[OffDataIn] = dataLength > 0 ? SCSI_IOCTL_DATA_IN : SCSI_IOCTL_DATA_UNSPECIFIED;
                Bytes.PutLE32(_in, OffTransferLength, (uint)dataLength);
                Bytes.PutLE32(_in, OffTimeout, timeoutSeconds);
                Bytes.PutLE64(_in, OffDataOffset, HeaderSize);
                Bytes.PutLE32(_in, OffSenseOffset, SptSize);
                Buffer.BlockCopy(cdb, 0, _in, OffCdb, cdb.Length);

                // Como no XBSX2: entrada com Length = cabeçalho + dados; saída com Length 0 e
                // capacidade cheia. Os IBuffer embrulham os arrays gerenciados, então a resposta do
                // driver aparece direto em _out.
                var input = _in.AsBuffer(0, HeaderSize + dataLength);
                var output = _out.AsBuffer(0, 0, _out.Length);
                uint received = await _device.SendIOControlAsync(_code, input, output);
                result.IoctlOk = true;

                if (received < SptSize)
                {
                    result.IoctlOk = false;
                    result.IoctlError = "resposta curta (" + received + " bytes)";
                    return result;
                }

                result.ScsiStatus = _out[OffScsiStatus];
                int senseLen = Math.Min(_out[OffSenseLength], SenseSize);
                uint senseOff = Bytes.LE32(_out, OffSenseOffset);
                if (result.ScsiStatus != 0 && senseLen > 0 && senseOff >= SptSize && senseOff + senseLen <= received)
                {
                    var sense = new byte[senseLen];
                    Buffer.BlockCopy(_out, (int)senseOff, sense, 0, senseLen);
                    result.Sense = SenseInfo.Parse(sense, senseLen);
                }

                if (result.ScsiStatus == 0 && dataLength > 0)
                {
                    uint transferred = Bytes.LE32(_out, OffTransferLength);
                    long available = (long)received - HeaderSize;
                    if (transferred > dataLength || transferred > available)
                    {
                        result.IoctlOk = false;
                        result.IoctlError = "DataTransferLength inválido (" + transferred + ", recebidos " + received + ")";
                        return result;
                    }
                    result.DataLength = (int)transferred;
                    if (dest != null)
                        Buffer.BlockCopy(_out, HeaderSize, dest, destOffset, (int)transferred);
                }
            }
            catch (Exception ex)
            {
                result.IoctlOk = false;
                result.HResult = ex.HResult;
                result.IoctlError = ex.Message;
            }
            finally
            {
                result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                _gate.Release();
            }
            return result;
        }

        public void Dispose()
        {
            // CustomDevice expõe IClosable (IDisposable em C#); o XBSX2 também o fecha.
            var d = _device as IDisposable;
            if (d != null) d.Dispose();
        }
    }
}
