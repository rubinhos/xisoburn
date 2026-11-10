// SPDX-License-Identifier: LGPL-2.1-or-later
// BD+ player side: player configuration (vm0 folder), traps, slots and the VM run sequences.
// Ports of libbdplus:
//   src/libbdplus/bdplus_config.c       Copyright (C) 2013 VideoLAN
//   src/libbdplus/bdsvm/interface.c     Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN
//   src/libbdplus/bdsvm/trap.c          Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN
//   src/libbdplus/bdsvm/slot.c          Copyright (C) 2008-2010 Accident
//   src/libbdplus/bdsvm/trap_helper.c   Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN
//   src/libbdplus/internal.c, bdplus.c  Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN
//   src/libbdplus/bdsvm/sha1.c          public domain (Steve Reid and others)
// LGPL-2.1-or-later.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using xISOBurn.Util;

namespace xISOBurn.Bluray.Bdplus
{
    /// <summary>Synchronous file access for the VM: reads 'count' bytes of a disc file at 'offset'; -1 if the file does not exist.</summary>
    internal delegate int BdplusFileRead(string path, long offset, byte[] buf, int bufOffset, int count);

    /// <summary>Player data that libbdplus loads from its vm0 configuration folder.</summary>
    internal sealed class BdplusConfig
    {
        public const string MemoryMapFile = "memory.map", AesKeysFile = "aes_keys.bin", EcdsaKeysFile = "ecdsa_keys.txt";

        public readonly List<byte[]> AesKeys = new List<byte[]>();
        public int NumAesKeys = -1;
        public readonly List<string[]> EcdsaKeys = new List<string[]>(); // d, Qx, Qy (40 hex digits each)
        public readonly byte[][] Dev = new byte[5][];
        public readonly List<RamArea> Ram = new List<RamArea>();
        public readonly List<string> Notes = new List<string>();

        internal sealed class RamArea
        {
            public uint Start;
            public byte[] Mem; // null for register files (PSR/GPR), which this port does not map
        }

        public bool HasMemoryMap { get { return Ram.Count > 0; } }

        public static BdplusConfig Load(string folder)
        {
            var c = new BdplusConfig();
            if (!Directory.Exists(folder))
            {
                c.Notes.Add("no BD+ player configuration (" + folder + ")");
                return c;
            }

            string path = Path.Combine(folder, AesKeysFile);
            if (File.Exists(path))
            {
                var keys = File.ReadAllBytes(path);
                int n = Math.Min(10, keys.Length / 16);
                for (int i = 0; i < n; i++)
                {
                    var k = new byte[16];
                    Buffer.BlockCopy(keys, i * 16, k, 0, 16);
                    c.AesKeys.Add(k);
                }
                c.NumAesKeys = n > 6 ? n : -1;
            }
            if (c.NumAesKeys < 0) c.Notes.Add("player AES keys not found (" + AesKeysFile + ")");

            path = Path.Combine(folder, EcdsaKeysFile);
            if (File.Exists(path))
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 3 && c.EcdsaKeys.Count < 4)
                        c.EcdsaKeys.Add(new[] { Cut(p[0]), Cut(p[1]), Cut(p[2]) });
                }
            }
            if (c.EcdsaKeys.Count == 0) c.Notes.Add("player ECDSA keys not found (" + EcdsaKeysFile + ")");

            int dev = 0;
            for (; dev < 5; dev++)
            {
                path = Path.Combine(folder, "device_discovery_" + (dev + 1) + ".bin");
                if (!File.Exists(path)) break;
                c.Dev[dev] = File.ReadAllBytes(path);
            }
            if (dev < 5) c.Notes.Add("player device discovery signatures not found (device_discovery_N.bin)");

            path = Path.Combine(folder, MemoryMapFile);
            if (File.Exists(path))
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    uint address;
                    if (p.Length < 2 || !uint.TryParse(p[0].StartsWith("0x") || p[0].StartsWith("0X") ? p[0].Substring(2) : p[0],
                            System.Globalization.NumberStyles.HexNumber, null, out address))
                        continue;
                    if (p[1] == "PSR" || p[1] == "GPR")
                    {
                        c.Ram.Add(new RamArea { Start = address, Mem = null });
                        continue;
                    }
                    string file = Path.Combine(folder, p[1]);
                    if (File.Exists(file)) c.Ram.Add(new RamArea { Start = address, Mem = File.ReadAllBytes(file) });
                    else c.Notes.Add("memory file " + p[1] + " not found");
                }
            }
            else c.Notes.Add("player memory map not found (" + MemoryMapFile + ")");
            return c;
        }

        private static string Cut(string s) { return s.Length > 40 ? s.Substring(0, 40) : s; }
    }

    /// <summary>The incremental SHA-1 of sha1.c; its state is exposed to the content code (sha_reference).</summary>
    internal sealed class Sha1State
    {
        public readonly uint[] State = new uint[5];
        public uint Count0, Count1;
        public readonly byte[] Buffer = new byte[64];

        public Sha1State() { Init(); }

        public void Init()
        {
            State[0] = 0x67452301; State[1] = 0xEFCDAB89; State[2] = 0x98BADCFE; State[3] = 0x10325476; State[4] = 0xC3D2E1F0;
            Count0 = Count1 = 0;
            Array.Clear(Buffer, 0, 64);
        }

        private static uint Rol(uint v, int b) { return (v << b) | (v >> (32 - b)); }

        private void Transform(byte[] data, int o)
        {
            var w = new uint[80];
            for (int i = 0; i < 16; i++)
                w[i] = ((uint)data[o + 4 * i] << 24) | ((uint)data[o + 4 * i + 1] << 16) | ((uint)data[o + 4 * i + 2] << 8) | data[o + 4 * i + 3];
            for (int i = 16; i < 80; i++) w[i] = Rol(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
            uint a = State[0], b = State[1], c = State[2], d = State[3], e = State[4];
            for (int i = 0; i < 80; i++)
            {
                uint f, k;
                if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
                else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
                else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
                else { f = b ^ c ^ d; k = 0xCA62C1D6; }
                uint t = Rol(a, 5) + f + e + k + w[i];
                e = d; d = c; c = Rol(b, 30); b = a; a = t;
            }
            State[0] += a; State[1] += b; State[2] += c; State[3] += d; State[4] += e;
        }

        public void Update(byte[] data, int offset, int len)
        {
            uint j = (Count0 >> 3) & 63;
            uint bits = (uint)len << 3;
            if ((Count0 += bits) < bits) Count1++;
            Count1 += (uint)len >> 29;
            int i;
            if (j + len > 63)
            {
                i = (int)(64 - j);
                System.Buffer.BlockCopy(data, offset, Buffer, (int)j, i);
                Transform(Buffer, 0);
                for (; i + 63 < len; i += 64) Transform(data, offset + i);
                j = 0;
            }
            else i = 0;
            System.Buffer.BlockCopy(data, offset + i, Buffer, (int)j, len - i);
        }

        public byte[] Final()
        {
            var finalcount = new byte[8];
            for (int i = 0; i < 8; i++)
                finalcount[i] = (byte)(((i >= 4 ? Count0 : Count1) >> ((3 - (i & 3)) * 8)) & 255);
            Update(new byte[] { 0x80 }, 0, 1);
            var zero = new byte[1];
            while ((Count0 & 504) != 448) Update(zero, 0, 1);
            Update(finalcount, 0, 8);
            var digest = new byte[20];
            for (int i = 0; i < 20; i++) digest[i] = (byte)((State[i >> 2] >> ((3 - (i & 3)) * 8)) & 255);
            return digest;
        }
    }

    /// <summary>
    /// libbdplus "bdplus_t": the VM, the player keys, the 500 slots, the conversion table, and the run
    /// sequences used to get the conversion table without menus (as in examples/bdplus_test.c).
    /// </summary>
    internal sealed class BdplusMachine
    {
        private const uint StatusOk = 0, StatusInvalidParameter = 0x80000001, StatusNotSupported = 0x80000002, StatusInternalError = 0x80FFFFFF;
        private const int NumSlots = 500, SlotSize = 256;
        private const int ShaBlockSize = 0x200, ShaDigestLength = 20;

        private readonly BdplusConfig _config;
        private readonly BdplusFileRead _read;
        private readonly Action<string> _log;
        private DlxVm _vm;

        private readonly byte[] _slots = new byte[NumSlots * SlotSize];
        private uint _attachedSlot, _freeSlot = NumSlots - 1;
        private readonly byte[] _attachedStatus = { 0, 7 };
        private readonly Dictionary<uint, Sha1State> _sha = new Dictionary<uint, Sha1State>();

        public byte[] VolumeId = new byte[16];
        public byte[] MediaKey = new byte[16];
        public ConvTable ConvTab;
        public int Gen, Date;
        public long InstructionBudget = 5000000000L; // per run sequence; protects the copy from a VM that never ends
        public int DebugLogLines;
        /// <summary>Polled between VM time slices; true stops the current run sequence.</summary>
        public Func<bool> Cancelled;

        public BdplusMachine(BdplusConfig config, BdplusFileRead read, Action<string> log)
        {
            _config = config;
            _read = read;
            _log = log;
        }

        // ------------------------------------------------------------ setup

        /// <summary>bdplus_load_svm: a fresh VM with BDSVM/00000.svm. Returns null on success, else the reason.</summary>
        public string LoadSvm(byte[] svm)
        {
            _vm = new DlxVm();
            _vm.OnTrap = Trap;
            return _vm.LoadSvm(svm, out Gen, out Date);
        }

        public void LoadSlots(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var b = File.ReadAllBytes(path);
                Buffer.BlockCopy(b, 0, _slots, 0, Math.Min(b.Length, _slots.Length));
            }
            catch (Exception) { }
        }

        public void SaveSlots(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, _slots);
            }
            catch (Exception) { }
        }

        public long Instructions { get { return _vm != null ? _vm.NumInstructions : 0; } }

        // ------------------------------------------------------------ run sequences (internal.c)

        private bool Step(ref long budgetEnd, out int ret)
        {
            ret = _vm.Run();
            if (ret < 0)
            {
                _log("BD+: VM execution error (unknown instruction at PC " + (_vm.PC - 4).ToString("X8") + ").");
                return false;
            }
            if (_vm.NumInstructions > budgetEnd)
            {
                _log("BD+: the VM ran too long; stopping it.");
                return false;
            }
            if (ret == 2 && Cancelled != null && Cancelled()) return false;
            return true;
        }

        private long Budget() { return _vm.NumInstructions + InstructionBudget; }

        public void RunIdle()
        {
            if (_vm == null) return;
            long end = Budget();
            int breaks = 0, ret;
            while (Step(ref end, out ret))
                if (ret == 2 && ++breaks >= 4) break;
        }

        public void RunInit()
        {
            if (_vm == null) return;
            _vm.SendEvent(DlxVm.EventStart, 0, 0, 0);
            RunIdle();
        }

        /// <summary>bdplus_run_convtab: emulates playback until the content code has sent the conversion table.</summary>
        public bool RunConvTab()
        {
            if (_vm == null) return false;
            _vm.SendEvent(DlxVm.EventStart, 0, 0, 0);
            long end = Budget();
            uint currentBreak = 0;
            int ret;
            bool keepRunning = true;
            while (keepRunning && Step(ref end, out ret))
            {
                if (ret != 2) continue;
                currentBreak++;
                switch (currentBreak)
                {
                    case 3:
                        _vm.SendEvent(DlxVm.EventPlaybackFile, 0, 0x0000FFFF, 0);
                        break;
                    case 0x31: case 0x32: case 0x33: case 0x34: case 0x35: case 0x36:
                    case 0x3B: case 0x3C: case 0x3D: case 0x3E: case 0x3F: case 0x40:
                        _vm.SendEvent(DlxVm.EventApplicationLayer, 0, 1, 0);
                        break;
                    case 0x42: _vm.SendEvent(DlxVm.EventPlaybackFile, 0, 2, 0); break;
                    case 0x65: _vm.SendEvent(DlxVm.EventPlaybackFile, 0, 4, 0); break;
                    case 0xF6:
                    case 0x158: _vm.SendEvent(DlxVm.EventPlaybackFile, 0, 0, 0); break;
                    case 0x19E: _vm.SendEvent(DlxVm.EventPlaybackFile, 0, 1, 0); break;
                    case 0x276: _vm.SendEvent(DlxVm.EventApplicationLayer, 0, 4, 1); break;
                    case 0x280: keepRunning = false; break;
                }
            }
            return ConvTab != null;
        }

        /// <summary>bdplus_run_title (BDPLUS_EVENT_TITLE).</summary>
        public void RunTitle(uint title)
        {
            if (_vm == null) return;
            if (ConvTab != null && title == 0xffff) return;
            long end = Budget();
            uint currentBreak = 0;
            int ret;
            while (Step(ref end, out ret))
            {
                if (ret != 2) continue;
                currentBreak++;
                if (currentBreak == 3) _vm.SendEvent(DlxVm.EventPlaybackFile, 0, title, 0);
                else if (currentBreak == 30) break;
            }
        }

        /// <summary>bdplus_run_m2ts: asks the content code for the key of every segment of one clip.</summary>
        public void RunM2ts(uint m2ts)
        {
            if (_vm == null || ConvTab == null) return;
            ConvTab.SetSegment(m2ts, 0);
            if (ConvTab.NumEntriesOfCurrent() < 1) return;
            long end = Budget();
            uint currentBreak = 0;
            int ret;
            int requests = 0, maxRequests = 4 * Math.Max(1, ConvTab.Tables[(int)ConvTab.CurrentTable].Segments.Length) + 8;
            uint lastTable = 0xFFFFFFFF, lastSegment = 0xFFFFFFFF;
            int sameInARow = 0;
            bool keepRunning = true;
            while (keepRunning && Step(ref end, out ret))
            {
                if (ret != 2) continue;
                currentBreak++;
                if (currentBreak <= 2) continue;
                uint table, segment;
                if (!ConvTab.NextSegment(out table, out segment) || table != m2ts)
                {
                    keepRunning = false;
                    break;
                }
                // Guard (not in libbdplus): a segment the content code never answers would loop forever.
                if (table == lastTable && segment == lastSegment)
                {
                    if (++sameInARow >= 3)
                    {
                        _log("BD+: no key for " + table.ToString("D5") + ".m2ts segment " + segment + "; skipping it.");
                        ConvTab.Tables[(int)ConvTab.CurrentTable].Segments[segment].Encrypted = false;
                        sameInARow = 0;
                        continue;
                    }
                }
                else sameInARow = 0;
                lastTable = table;
                lastSegment = segment;
                if (++requests > maxRequests) break;
                _vm.SendEvent(DlxVm.EventComputeSP, 0, table, segment);
                currentBreak = 1;
            }
        }

        public void RunShutdown()
        {
            if (_vm == null) return;
            _vm.SendEvent(DlxVm.EventShutdown, 0, 0, 0);
            RunIdle();
        }

        // ------------------------------------------------------------ slots (slot.c, internal.c)

        private int SlotOffset(uint slot) { return (int)slot * SlotSize; }

        private void GetSlot(uint slot, byte[] dst, int dstOffset)
        {
            if (slot == 0xFFFFFFFF) slot = _attachedSlot;
            if (slot >= NumSlots) return;
            Buffer.BlockCopy(_slots, SlotOffset(slot), dst, dstOffset, SlotSize);
            if (slot != _attachedSlot) Array.Clear(dst, dstOffset + 48, 20); // authHash
        }

        private uint NewSlot()
        {
            _attachedSlot = _freeSlot;
            _freeSlot--;
            int o = SlotOffset(_attachedSlot);
            Array.Clear(_slots, o, SlotSize);
            Buffer.BlockCopy(VolumeId, 0, _slots, o, 16);
            Buffer.BlockCopy(VolumeId, 0, _slots, o + 16, 16);
            if (_attachedStatus[1] > 2) _attachedStatus[1]--;
            _attachedStatus[0] = _attachedStatus[1];
            return _attachedSlot;
        }

        private uint SlotAttach(uint slot, uint codeLen)
        {
            uint pc = _vm.PC - 4;
            if (slot == 0xFFFFFFFF) return NewSlot();
            if (slot >= NumSlots) return StatusInvalidParameter;
            var hash = new byte[4 * (1 + 1 + 4) + 4 * codeLen];
            Bytes.PutBE32(hash, 0, pc);
            Bytes.PutBE32(hash, 4, _vm.IF);
            Buffer.BlockCopy(_vm.Mem, (int)_vm.CodeStart, hash, 8, 16);
            Buffer.BlockCopy(_vm.Mem, (int)pc, hash, 24, (int)(codeLen * 4));
            var digest = Hash.Sha1(Hash.Sha1(hash));
            if (Bytes.Equal(_slots, SlotOffset(slot) + 48, digest, 0, 20))
            {
                _attachedSlot = slot;
                return StatusOk;
            }
            _attachedSlot = 0;
            return StatusInvalidParameter;
        }

        private void SlotWrite(uint src)
        {
            var slot = new byte[SlotSize];
            GetSlot(0xFFFFFFFF, slot, 0);
            Buffer.BlockCopy(_vm.Mem, (int)src + 16, slot, 16, 16);   // mMediaID
            Buffer.BlockCopy(_vm.Mem, (int)src + 32, slot, 32, 16);   // privateData
            Buffer.BlockCopy(_vm.Mem, (int)src + 48, slot, 48, 20);   // authHash
            Buffer.BlockCopy(_vm.Mem, (int)src + 76, slot, 76, 180);  // payload
            uint counter = Bytes.BE32(slot, 72) + 1;
            Bytes.PutBE32(slot, 72, counter);
            Buffer.BlockCopy(slot, 0, _slots, SlotOffset(_attachedSlot), SlotSize);
        }

        // ------------------------------------------------------------ traps (interface.c)

        private static bool BadAddress(uint p, uint len)
        {
            return p >= DlxVm.MemorySize || p + len > DlxVm.MemorySize || p + len < p;
        }

        private static bool BadAligned(uint p, uint len) { return BadAddress(p, len) || (p & 3) != 0; }

        private string CString(uint addr, int max)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < max; i++)
            {
                byte c = _vm.Mem[(addr + (uint)i) & DlxVm.Mask1];
                if (c == 0) break;
                sb.Append((char)c);
            }
            return sb.ToString();
        }

        private void Trap(DlxVm vm, uint trap)
        {
            uint sp = vm.R[29] & DlxVm.Mask4;
            Func<uint, uint> arg = i => vm.Fetch4((sp + 4 * i) & DlxVm.Mask4);
            uint result = StatusOk;
            uint src, dst, len, fsize, ev = vm.EventCurrent;
            bool playerEvent = ev == 0x0000 || ev == 0x0010 || ev == 0x0110;

            switch (trap)
            {
                case 0x010: // TRAP_Finished
                    if (vm.EventCurrent == DlxVm.EventComputeSP && ConvTab != null)
                        ConvTab.DecryptSegment(vm.Mem, 0x10, 0x20);
                    if (vm.EventProcessing)
                    {
                        vm.SetPC(vm.R[28] & DlxVm.Mask4);
                        vm.WD = 0xFA0;
                    }
                    vm.EventProcessing = false;
                    vm.EventCurrent = 0x88888888;
                    return;

                case 0x020: // TRAP_ConversionTable(len, src)
                    len = arg(0);
                    src = arg(1);
                    if (BadAligned(src, len)) result = StatusInvalidParameter;
                    else if (len > 0x100000) result = StatusNotSupported;
                    if (len != 0 && len <= DlxVm.MemorySize)
                    {
                        var table = vm.ReadWrapped(src & DlxVm.Mask4, (int)len);
                        var ct = ConvTable.Parse(table, (int)len);
                        if (ct != null)
                        {
                            if (ConvTab == null) ConvTab = ct;
                            else ConvTab.Merge(ct);
                        }
                    }
                    break;

                case 0x110: // TRAP_Aes(dst, src, len, key, opOrKeyID)
                    dst = arg(0); src = arg(1); len = arg(2);
                    uint key = arg(3);
                    if (!playerEvent) result = StatusInternalError;
                    else if (BadAddress(dst, 16 * len) || BadAddress(src, 16 * len) || BadAddress(key, 16) ||
                             (dst + 16 * len > src && dst < src + 16 * len && dst != src))
                        result = StatusInvalidParameter;
                    else result = TrapAes(dst, src, len, key, arg(4));
                    break;

                case 0x120: // TRAP_PrivateKey(keyID, dst, src, srcLen, controlWord)
                    dst = arg(1); src = arg(2); len = arg(3);
                    if (!playerEvent) result = StatusInternalError;
                    else if (BadAddress(dst, 40) || BadAddress(src, len) || dst < src) result = StatusInvalidParameter;
                    else result = TrapPrivateKey(arg(0), dst, src, len, arg(4));
                    break;

                case 0x130: // TRAP_Random(dst, len)
                    dst = arg(0); len = arg(1);
                    if (BadAddress(dst, len)) result = StatusInvalidParameter;
                    else Rng.Fill(vm.Mem, (int)dst, (int)len);
                    break;

                case 0x140: // TRAP_Sha(dst, src, len, op)
                    dst = arg(0); src = arg(1); len = arg(2);
                    if (BadAddress(src, len) || BadAligned(dst, 0x200) || (dst + 0x200 > src && dst < src + len))
                        result = StatusInvalidParameter;
                    else result = TrapSha1(dst & DlxVm.Mask4, src, len, arg(3));
                    break;

                case 0x210: // TRAP_AddWithCarry(dst, src, len)
                    dst = arg(0); src = arg(1); len = arg(2);
                    if (BadAligned(dst, 4 * len) || BadAligned(src, 4 * len) || (src < dst + 4 * len && src + 4 * len > dst && src != dst))
                        result = StatusInvalidParameter;
                    else if (len != 0)
                    {
                        uint carry = 0;
                        for (int i = (int)len - 1; i >= 0; i--)
                        {
                            ulong sum = (ulong)vm.Fetch4(src + 4 * (uint)i) + vm.Fetch4(dst + 4 * (uint)i) + carry;
                            carry = sum > 0xFFFFFFFF ? 1u : 0u;
                            vm.Store4(dst + 4 * (uint)i, (uint)sum);
                        }
                        result = carry;
                    }
                    break;

                case 0x220: // TRAP_MultiplyWithRipple(dst, src, len, multiplicand)
                    dst = arg(0); src = arg(1); len = arg(2);
                    if (BadAligned(dst, 4 * len + 4) || BadAligned(src, 4 * len) || (dst + 4 * len + 4 > src && dst < src + 4 * len))
                        result = StatusInvalidParameter;
                    else
                    {
                        uint mul = arg(3);
                        if (len == 0) vm.Store4(dst, 0);
                        else
                        {
                            ulong carry = 0;
                            for (int i = (int)len - 1; i >= 0; i--)
                            {
                                ulong sum = (ulong)vm.Fetch4(src + 4 * (uint)i) * mul + carry;
                                carry = sum >> 32;
                                vm.Store4(dst + 4 * (uint)(i + 1), (uint)sum);
                            }
                            vm.Store4(dst, (uint)carry);
                        }
                    }
                    break;

                case 0x230: // TRAP_XorBlock(dst, src, len)
                    dst = arg(0); src = arg(1); len = arg(2);
                    if (BadAligned(dst, 4 * len) || BadAligned(src, 4 * len) || (dst + 4 * len > src && dst < src + 4 * len))
                        result = StatusInvalidParameter;
                    else
                        for (uint i = 0; i < 4 * len; i++) vm.Mem[dst + i] ^= vm.Mem[src + i];
                    break;

                case 0x310: // TRAP_Memmove(dst, src, len)
                    dst = arg(0); src = arg(1); len = arg(2);
                    if (BadAddress(dst, len) || BadAddress(src, len)) result = StatusInvalidParameter;
                    else if (src != dst && len != 0) Buffer.BlockCopy(vm.Mem, (int)src, vm.Mem, (int)dst, (int)len);
                    break;

                case 0x320: // TRAP_MemSearch(region, regionLen, searchData, searchDataLen, *dst)
                {
                    uint region = arg(0), regionLen = arg(1), search = arg(2), searchLen = arg(3);
                    dst = arg(4);
                    if (BadAddress(region, regionLen) || BadAddress(search, searchLen) || BadAligned(dst, 4))
                        result = StatusInvalidParameter;
                    else
                    {
                        uint found = 0;
                        if (regionLen != 0 && searchLen != 0 && regionLen >= searchLen)
                        {
                            for (uint i = 0; i < regionLen - searchLen + 1; i++)
                            {
                                uint j;
                                for (j = 0; j < searchLen; j++)
                                    if (vm.Mem[region + i + j] != vm.Mem[search + j]) break;
                                if (j == searchLen) { found = region + i; break; }
                            }
                        }
                        vm.Store4(dst & DlxVm.Mask4, found);
                    }
                    break;
                }

                case 0x330: // TRAP_Memset(dst, fill, len)
                    dst = arg(0); len = arg(2);
                    if (BadAddress(dst, len)) result = StatusInvalidParameter;
                    else
                    {
                        byte fill = (byte)arg(1);
                        for (uint i = 0; i < len; i++) vm.Mem[dst + i] = fill;
                    }
                    break;

                case 0x410: // TRAP_SlotAttach(slot, codeLen)
                {
                    _attachedSlot = 0;
                    _attachedStatus[0] = 0;
                    uint pc = vm.PC - 4;
                    len = arg(1);
                    if (BadAligned(pc, 4 * len)) result = StatusInvalidParameter;
                    else result = SlotAttach(arg(0), len);
                    break;
                }

                case 0x420: // TRAP_SlotRead(dst, slot)
                {
                    dst = arg(0);
                    uint slot = arg(1);
                    len = slot == 0xFFFFFFFF ? 0x0Cu : (uint)SlotSize;
                    if (BadAddress(dst, len)) result = StatusInvalidParameter;
                    else if (slot == 0xFFFFFFFF)
                    {
                        uint d = dst & DlxVm.Mask4;
                        vm.Store4(d, _attachedSlot);
                        vm.Store4(d + 4, _attachedStatus[0]);
                        vm.Store4(d + 8, _attachedStatus[1]);
                    }
                    else if (slot >= NumSlots) result = StatusInvalidParameter;
                    else if ((dst & DlxVm.Mask4) + SlotSize <= DlxVm.MemorySize) GetSlot(slot, vm.Mem, (int)(dst & DlxVm.Mask4));
                    break;
                }

                case 0x430: // TRAP_SlotWrite(newContents)
                    src = arg(0);
                    if (BadAddress(src, SlotSize) || (src & DlxVm.Mask4) + SlotSize > DlxVm.MemorySize) result = StatusInvalidParameter;
                    else SlotWrite(src & DlxVm.Mask4);
                    break;

                case 0x510: // TRAP_ApplicationLayer(dev, opID, buf)
                {
                    src = arg(2);
                    uint devId = arg(0), op = arg(1);
                    if (playerEvent) result = StatusInternalError;
                    else if (BadAligned(src, 4)) result = StatusInvalidParameter;
                    else if (devId >= 3 || op > 1 || (devId == 1 && op == 1)) result = StatusInvalidParameter;
                    // No player registers are attached (as when libbdplus runs without libbluray): OK.
                    break;
                }

                case 0x520: // TRAP_Discovery(dev, qID, buf, *len)
                {
                    uint lenPtr = arg(3);
                    fsize = vm.Fetch4(lenPtr & DlxVm.Mask4);
                    dst = arg(2);
                    if (BadAligned(lenPtr, 4) || BadAligned(dst, fsize)) result = StatusInvalidParameter;
                    else
                    {
                        result = TrapDiscovery(arg(0), arg(1), dst & DlxVm.Mask4, ref fsize);
                        if (result == StatusOk) vm.Store4(lenPtr & DlxVm.Mask4, fsize);
                    }
                    break;
                }

                case 0x530: // TRAP_DiscoveryRAM(src, dst, len)
                    src = arg(0); dst = arg(1); len = arg(2);
                    if (len > 0x100000) result = StatusNotSupported;
                    else if (BadAddress(dst, len)) result = StatusInvalidParameter;
                    else if (len == 0) result = StatusOk;
                    else if ((ulong)src + len > 0x100000000UL || src >= 0x400000 || src + len >= 0x400000) result = StatusInvalidParameter;
                    else result = TrapDiscoveryRam(src, dst, len);
                    break;

                case 0x540: // TRAP_LoadContentCode(name, block, offset, *len, dst)
                {
                    src = arg(0);
                    uint lenPtr = arg(3);
                    fsize = vm.Fetch4(lenPtr & DlxVm.Mask4);
                    if (!playerEvent) result = StatusInternalError;
                    else if (BadAligned(lenPtr, 4) || BadAddress(src, 5)) result = StatusInvalidParameter;
                    else result = TrapLoadContentCode(CString(src, 64), arg(1), ref fsize, arg(4));
                    if (result == StatusOk) vm.Store4(lenPtr & DlxVm.Mask4, fsize);
                    break;
                }

                case 0x550: // TRAP_MediaCheck(name, nameLen, offHigh, offLow, *len, dst)
                {
                    src = arg(0);
                    uint nameLen = arg(1), lenPtr = arg(4);
                    dst = arg(5);
                    fsize = vm.Fetch4(lenPtr & DlxVm.Mask4);
                    if (vm.EventCurrent != 0) result = StatusInternalError;
                    else if (BadAddress(lenPtr, 4) || BadAligned(lenPtr, 4)) result = StatusInvalidParameter;
                    else if (fsize > 0x200000) result = StatusNotSupported;
                    else if ((fsize & 0x1FF) != 0 || BadAddress(dst, ShaDigestLength * (fsize / ShaBlockSize)) ||
                             BadAddress(src, nameLen) || nameLen == 0 || nameLen > 0x400)
                        result = StatusInvalidParameter;
                    else
                        result = TrapMediaCheck(CString(src, (int)nameLen), ((ulong)arg(2) << 32) | arg(3), ref fsize, dst & DlxVm.Mask1);
                    if (result == StatusOk) vm.Store4(lenPtr & DlxVm.Mask4, fsize);
                    break;
                }

                case 0x8010: // TRAP_DebugLog(txt, len)
                    src = arg(0); len = arg(1);
                    if (BadAddress(src, len)) result = StatusInvalidParameter;
                    else
                    {
                        if (DebugLogLines < 20)
                        {
                            DebugLogLines++;
                            _log("BD+ content code: " + CString(src, (int)Math.Min(len, 200u)).Trim());
                        }
                        result = StatusInternalError;
                    }
                    break;

                default:
                    result = StatusNotSupported;
                    break;
            }

            vm.R[1] = result;
            if (result != StatusInternalError && !vm.EventProcessing)
                vm.WD = vm.WD >= 0x140 ? vm.WD - 0x140 : 0;
        }

        // ------------------------------------------------------------ trap implementations (trap.c)

        private uint TrapAes(uint dst, uint src, uint len, uint key, uint opOrKeyId)
        {
            if (opOrKeyId > 0xFFF10002) return StatusInvalidParameter;
            if (opOrKeyId < 0xFFF10000 && opOrKeyId > 6) return StatusInvalidParameter;
            var mem = _vm.Mem;
            byte[] k = new byte[16];
            switch (opOrKeyId)
            {
                case 0xFFF10000: // encrypt
                    Buffer.BlockCopy(mem, (int)key, k, 0, 16);
                    using (var t = Aes128.EcbEncryptor(k)) Aes128.Run(t, mem, (int)src, (int)(16 * len), mem, (int)dst);
                    return StatusOk;
                case 0xFFF10002: // decrypt with the media key
                case 0xFFF10001: // decrypt
                    if (opOrKeyId == 0xFFF10002) Buffer.BlockCopy(MediaKey, 0, k, 0, 16);
                    else Buffer.BlockCopy(mem, (int)key, k, 0, 16);
                    using (var t = Aes128.EcbDecryptor(k)) Aes128.Run(t, mem, (int)src, (int)(16 * len), mem, (int)dst);
                    return StatusOk;
                default: // decrypt the key with a player key, then the data with it
                    if ((int)opOrKeyId >= _config.NumAesKeys) return StatusInvalidParameter;
                    var enc = new byte[16];
                    Buffer.BlockCopy(mem, (int)key, enc, 0, 16);
                    var dk = Aes128.Decrypt(_config.AesKeys[(int)opOrKeyId], enc);
                    using (var t = Aes128.EcbDecryptor(dk)) Aes128.Run(t, mem, (int)src, (int)(16 * len), mem, (int)dst);
                    return StatusOk;
            }
        }

        private uint TrapPrivateKey(uint keyId, uint dst, uint src, uint srcLen, uint controlWord)
        {
            if (keyId > 1 || keyId >= _config.EcdsaKeys.Count) return StatusInvalidParameter;
            var message = new byte[16 + srcLen];
            message[0] = (byte)'B'; message[1] = (byte)'D'; message[2] = (byte)'S'; message[3] = (byte)'V';
            message[4] = (byte)'M'; message[5] = (byte)'_'; message[6] = (byte)'P'; message[7] = (byte)'K';
            Bytes.PutBE32(message, 8, controlWord);
            Bytes.PutBE32(message, 12, srcLen);
            Buffer.BlockCopy(_vm.Mem, (int)src, message, 16, (int)srcLen);
            BigInteger d;
            try { d = Big.FromHex(_config.EcdsaKeys[(int)keyId][0]); }
            catch (FormatException) { return StatusInvalidParameter; }
            var sig = EcCurve.Bdplus.Sign(d, Hash.Sha1(message));
            Buffer.BlockCopy(sig, 0, _vm.Mem, (int)dst, 40);
            return StatusOk;
        }

        private uint TrapSha1(uint dst, uint src, uint len, uint op)
        {
            var mem = _vm.Mem;
            Sha1State ctx;
            switch (op)
            {
                case 1: // SHA_INIT
                    if (!_sha.TryGetValue(dst, out ctx)) { ctx = new Sha1State(); _sha[dst] = ctx; }
                    Array.Clear(mem, (int)dst, 352);
                    ctx.Init();
                    return TrapSha1(dst, src, len, 0);
                case 0: // SHA_UPDATE
                    if (!_sha.TryGetValue(dst, out ctx)) { ctx = new Sha1State(); _sha[dst] = ctx; }
                    ctx.Update(mem, (int)src, (int)len);
                    ShaReference(dst, ctx);
                    return StatusOk;
                case 2: // SHA_FINAL
                    if (!_sha.TryGetValue(dst, out ctx)) { ctx = new Sha1State(); _sha[dst] = ctx; }
                    TrapSha1(dst, src, len, 0);
                    var digest = ctx.Final();
                    Buffer.BlockCopy(digest, 0, mem, (int)dst, 20);
                    _sha.Remove(dst);
                    return StatusOk;
                case 3: // SHA_BLOCK
                    var h = Hash.Sha1(mem, (int)src, (int)len);
                    Buffer.BlockCopy(h, 0, mem, (int)dst, 20);
                    return StatusOk;
                default:
                    return StatusInvalidParameter;
            }
        }

        /// <summary>sha_reference: makes the context in VM memory look like the reference player's.</summary>
        private void ShaReference(uint dst, Sha1State sha)
        {
            var mem = _vm.Mem;
            for (int i = 0; i < 20; i++) mem[dst + i] = (byte)((sha.State[i >> 2] >> ((3 - (i & 3)) * 8)) & 255);
            Buffer.BlockCopy(sha.Buffer, 0, mem, (int)dst + 20, (int)((sha.Count0 >> 3) & 63));
            uint total = sha.Count0 >> 3;
            mem[dst + 340] = (byte)(total & 0xFF);
            mem[dst + 348] = (byte)((total * 8) & 0xFF);
            mem[dst + 349] = (byte)(((total * 8) & 0xFF00) >> 8);
        }

        private void CopyToVm(byte[] data, uint dst)
        {
            if (data == null) return;
            _vm.WriteWrapped(dst, data, 0, data.Length);
        }

        private uint TrapDiscovery(uint dev, uint qId, uint dst, ref uint len)
        {
            if (len == 0) return StatusInvalidParameter;
            if (dev != 1 && dev != 2) return StatusInvalidParameter;
            if (dev == 1 && qId != 1 && qId != 2 && qId != 3) return StatusInvalidParameter;
            if (dev == 2 && qId != 0 && qId != 1 && qId != 2) return StatusNotSupported;
            if (dev == 1)
            {
                var d = _config.Dev[qId - 1];
                uint size = d != null ? (uint)d.Length : 0;
                if (len < size) return StatusInvalidParameter;
                CopyToVm(d, dst);
                if (qId == 3)
                {
                    // Volume ID at offset 24, local time at offset 0.
                    _vm.WriteWrapped(dst + 24, VolumeId, 0, 16);
                    var now = DateTime.Now;
                    var t = new byte[8];
                    t[0] = (byte)(now.Year >> 8);
                    t[1] = (byte)now.Year;
                    t[2] = (byte)(now.Month - 1);
                    t[3] = (byte)now.Day;
                    t[4] = (byte)now.Hour;
                    t[5] = (byte)now.Minute;
                    t[6] = (byte)now.Second;
                    t[7] = (byte)(((byte)(now.Millisecond * 1000)) / 10);
                    _vm.WriteWrapped(dst, t, 0, 8);
                }
                len = size;
                return StatusOk;
            }
            if (qId == 0) { CopyToVm(_config.Dev[3], dst); return StatusOk; }
            if (qId == 1) { CopyToVm(_config.Dev[4], dst); return StatusOk; }
            return StatusInvalidParameter;
        }

        private uint TrapDiscoveryRam(uint src, uint dst, uint len)
        {
            if (!_config.HasMemoryMap) return StatusInvalidParameter;
            var mem = _vm.Mem;
            for (uint i = 0; i < len; i++)
            {
                uint address = src + i;
                BdplusConfig.RamArea ram = null;
                foreach (var a in _config.Ram)
                {
                    uint size = a.Mem != null ? (uint)a.Mem.Length : 0;
                    if (address >= a.Start && address < a.Start + size) { ram = a; break; }
                }
                if (ram != null)
                {
                    mem[(dst + i) & DlxVm.Mask1] = ram.Mem[address - ram.Start];
                    if (address >= 0x00250000 && address <= 0x0028FFFF)
                        mem[(dst + i) & DlxVm.Mask1] ^= (byte)((3 * address * address + 1) & 0xFF);
                }
                else if (address > 0x400000)
                {
                    mem[(dst + i) & DlxVm.Mask1] = 0;
                }
            }
            return StatusOk;
        }

        private uint TrapLoadContentCode(string name, uint section, ref uint len, uint dst)
        {
            string fname = "BDSVM/" + name + ".svm";
            long offset = 0x18 + (long)section * 0x200000;
            if (len > DlxVm.MemorySize) return StatusInvalidParameter;
            var buf = new byte[len];
            int got = _read(fname, offset, buf, 0, (int)len);
            if (got < 0 || got != len) return StatusInvalidParameter;
            _vm.WriteWrapped(dst & DlxVm.Mask1, buf, 0, got);
            len = (uint)got;
            return StatusOk;
        }

        private uint TrapMediaCheck(string name, ulong offset, ref uint len, uint dst)
        {
            var block = new byte[ShaBlockSize];
            if (_read(name, 0, block, 0, 0) < 0) return StatusInvalidParameter; // file not found
            uint j;
            for (j = 0; j < len / ShaBlockSize; j++)
            {
                int got = _read(name, (long)offset + j * ShaBlockSize, block, 0, ShaBlockSize);
                if (got != ShaBlockSize) break;
                var h = Hash.Sha1(block);
                _vm.WriteWrapped(dst + j * ShaDigestLength, h, 0, ShaDigestLength);
            }
            len = j * ShaBlockSize;
            return StatusOk;
        }
    }
}
