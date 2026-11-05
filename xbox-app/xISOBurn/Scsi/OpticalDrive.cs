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
    /// Access to the Xbox optical drive through Windows.Devices.Custom + SCSI pass-through.
    ///
    /// Why this way: console apps are refused CreateFile("\\.\X:") and the IOCTL_CDROM/IOCTL_DVD
    /// requests; the route that opens in Dev Mode is the CD-ROM device interface opened as a
    /// CustomDevice. This mirrors pcsx2/CDVD/Windows/IOCtlSrcXbox.cpp from XBSX2 (PR #11):
    ///   - DeviceInformation.FindAllAsync with the AQS filter for the CD-ROM interface class;
    ///   - CustomDevice.FromIdAsync(id, ReadWrite, Shared);
    ///   - IOControlCode(IOCTL_SCSI_BASE=0x0004, function 0x401, ReadWrite, Buffered)
    ///     = IOCTL_SCSI_PASS_THROUGH (0x4D004), the BUFFERED variant (not _DIRECT): the structure,
    ///     the sense data and the data travel in a single contiguous buffer.
    /// </summary>
    internal sealed class OpticalDrive : IDisposable
    {
        /// <summary>GUID_DEVINTERFACE_CDROM (the same one declared as a DeviceCapability in the manifest).</summary>
        public const string CdromInterfaceClass = "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}";

        /// <summary>AQS filter identical to XBSX2's DRIVE_SELECTOR.</summary>
        public const string Selector = "System.Devices.InterfaceClassGuid:=\"" + CdromInterfaceClass + "\"";

        public const int SectorSize = 2048;

        // SCSI_PASS_THROUGH layout on x64 (ntddscsi.h), sizeof = 56:
        //   0 USHORT Length          2 UCHAR ScsiStatus      3 UCHAR PathId
        //   4 UCHAR TargetId         5 UCHAR Lun             6 UCHAR CdbLength
        //   7 UCHAR SenseInfoLength  8 UCHAR DataIn          (9..11 padding)
        //  12 ULONG DataTransferLength                       16 ULONG TimeOutValue
        //  (20..23 padding)         24 ULONG_PTR DataBufferOffset (8 bytes on x64)
        //  32 ULONG SenseInfoOffset 36 UCHAR Cdb[16]         (52..55 padding)
        // The app is x64 only; a 32-bit process would need a different layout (SCSI_PASS_THROUGH32).
        private const int SptSize = 56;
        private const int OffLength = 0, OffScsiStatus = 2, OffCdbLength = 6, OffSenseLength = 7, OffDataIn = 8;
        private const int OffTransferLength = 12, OffTimeout = 16, OffDataOffset = 24, OffSenseOffset = 32, OffCdb = 36;
        private const int SenseSize = 32;
        /// <summary>Header = structure + sense; the data starts right after it (as in XBSX2).</summary>
        public const int HeaderSize = SptSize + SenseSize; // 88

        private const byte SCSI_IOCTL_DATA_IN = 1;
        private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;

        private readonly CustomDevice _device;
        private readonly IOControlCode _code;
        private readonly byte[] _in;
        private readonly byte[] _out;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        /// <summary>Largest data transfer this object accepts (buffer size).</summary>
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

        /// <summary>Opens the drive. Throws (with an HRESULT) if the system refuses.</summary>
        public static async Task<OpticalDrive> OpenAsync(string deviceId, int maxTransferBytes)
        {
            var device = await CustomDevice.FromIdAsync(deviceId, DeviceAccessMode.ReadWrite, DeviceSharingMode.Shared);
            if (device == null)
                // XBSX2 treats "null without an exception" as access denied (missing capability/consent).
                throw new UnauthorizedAccessException("CustomDevice.FromIdAsync returned null (access denied?)");
            return new OpticalDrive(device, deviceId, maxTransferBytes);
        }

        /// <summary>Finds the first CD-ROM interface and opens it. Throws if there is no drive.</summary>
        public static async Task<OpticalDrive> FindAndOpenAsync(int maxTransferBytes)
        {
            var devices = await FindAsync();
            if (devices.Count == 0)
                throw new InvalidOperationException("No optical drive found (CD-ROM interface).");
            return await OpenAsync(devices[0].Id, maxTransferBytes);
        }

        /// <summary>
        /// Sends a CDB. If dataLength > 0 it is a read command (DATA IN) and the data is copied to
        /// dest[destOffset..]. Never throws: errors come back in the ScsiResult.
        /// </summary>
        public async Task<ScsiResult> SendAsync(byte[] cdb, int dataLength, byte[] dest, int destOffset, uint timeoutSeconds)
        {
            if (cdb.Length == 0 || cdb.Length > 16) throw new ArgumentException("A CDB must be 1..16 bytes");
            if (dataLength < 0 || dataLength > MaxTransferBytes) throw new ArgumentOutOfRangeException("dataLength");

            await _gate.WaitAsync();
            try
            {
                var result = await SendCoreAsync(cdb, dataLength, dest, destOffset, timeoutSeconds);

                // In the spike, reads past the end of the disc came back as a "short response (3 bytes)"
                // with no sense data. In that case we ask for the sense explicitly (REQUEST SENSE, 0x03).
                // If the driver already consumed it (auto-sense), the drive answers "NO SENSE" and the
                // information is lost, but trying costs little: it is fast and has no side effects.
                if (!result.Success && result.Sense == null && cdb[0] != 0x03)
                {
                    var sense = new byte[18];
                    var rs = await SendCoreAsync(ScsiCommands.RequestSense(18), 18, sense, 0, 10);
                    if (rs.Success && rs.DataLength >= 8)
                    {
                        var parsed = SenseInfo.Parse(sense, rs.DataLength);
                        if (parsed != null && parsed.Key != 0)
                        {
                            parsed.FromRequestSense = true;
                            result.Sense = parsed;
                        }
                    }
                }
                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task<ScsiResult> SendCoreAsync(byte[] cdb, int dataLength, byte[] dest, int destOffset, uint timeoutSeconds)
        {
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

                // As in XBSX2: input Length = header + data; output Length 0 with full capacity.
                // The IBuffers wrap the managed arrays, so the driver's reply lands directly in _out.
                var input = _in.AsBuffer(0, HeaderSize + dataLength);
                var output = _out.AsBuffer(0, 0, _out.Length);
                uint received = await _device.SendIOControlAsync(_code, input, output);
                result.IoctlOk = true;

                if (received < SptSize)
                {
                    // Logging the raw bytes helps to understand what the Xbox driver returns in this case.
                    var raw = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min((int)received, 16); i++) raw.Append(_out[i].ToString("x2")).Append(' ');
                    result.IoctlOk = false;
                    result.IoctlError = "short response (" + received + " bytes: " + raw.ToString().Trim() + ")";
                    return result;
                }

                result.ScsiStatus = _out[OffScsiStatus];
                int senseLen = Math.Min((int)_out[OffSenseLength], SenseSize);
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
                        result.IoctlError = "invalid DataTransferLength (" + transferred + ", received " + received + ")";
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
            }
            return result;
        }

        public void Dispose()
        {
            // The SDK metadata does not declare IClosable on CustomDevice, but XBSX2 queries for it and
            // closes it at runtime (try_as<IClosable>). Same effect here: a dynamic check through object.
            object boxed = _device;
            var d = boxed as IDisposable;
            if (d != null) d.Dispose();
        }
    }
}
