// SPDX-License-Identifier: LGPL-2.1-or-later
// BD+ virtual machine (DLX instruction set), events and content code loader. Ports of libbdplus:
//   src/libbdplus/bdsvm/dlx.c     Copyright (C) 2008-2010 Accident
//   src/libbdplus/bdsvm/event.c   Copyright (C) 2008-2010 Accident
//   src/libbdplus/bdsvm/loader.c  Copyright (C) 2008-2010 Accident, (C) 2013 VideoLAN
// LGPL-2.1-or-later. Memory is big-endian; all register arithmetic is unsigned 32-bit.

using System;

namespace xISOBurn.Bluray.Bdplus
{
    internal sealed class DlxVm
    {
        public const uint MemorySize = 0x400000;
        public const uint Mask1 = 0x3FFFFF, Mask2 = 0x3FFFFE, Mask4 = 0x3FFFFC;
        public const uint StartAddr = 0x1000;

        public const uint EventStart = 0x000, EventShutdown = 0x010, EventPlaybackFile = 0x110,
                          EventApplicationLayer = 0x210, EventComputeSP = 0x220;

        public readonly byte[] Mem = new byte[MemorySize];
        public uint PC;
        public readonly uint[] R = new uint[32];
        public uint IF;
        public int WD;
        public uint CodeStart;
        public bool EventProcessing;
        public uint EventCurrent;
        public uint Trap;
        public uint NumTraps, NumBreaks;
        public long NumInstructions;

        /// <summary>Handles a TRAP instruction (interface_trap).</summary>
        public Action<DlxVm, uint> OnTrap;

        public DlxVm()
        {
            SetPC(StartAddr);
        }

        public void SetPC(uint pc)
        {
            PC = pc;
            if (CodeStart == 0) CodeStart = pc;
        }

        // ------------------------------------------------------------ memory (big-endian)

        public uint Fetch4(uint a)
        {
            return ((uint)Mem[a] << 24) | ((uint)Mem[a + 1] << 16) | ((uint)Mem[a + 2] << 8) | Mem[a + 3];
        }

        public void Store4(uint a, uint v)
        {
            Mem[a] = (byte)(v >> 24);
            Mem[a + 1] = (byte)(v >> 16);
            Mem[a + 2] = (byte)(v >> 8);
            Mem[a + 3] = (byte)v;
        }

        /// <summary>Copies into VM memory, wrapping at the end (content code relies on the wrap).</summary>
        public void WriteWrapped(uint addr, byte[] src, int srcOffset, int length)
        {
            for (int i = 0; i < length; i++) Mem[(addr + (uint)i) & Mask1] = src[srcOffset + i];
        }

        public byte[] ReadWrapped(uint addr, int length)
        {
            var o = new byte[length];
            for (int i = 0; i < length; i++) o[i] = Mem[(addr + (uint)i) & Mask1];
            return o;
        }

        // ------------------------------------------------------------ events (event.c)

        public void SendEvent(uint eventId, uint arg1, uint table, uint segment)
        {
            Store4(0x0, eventId);
            Store4(0x4, arg1);
            Store4(0x8, table);
            if (eventId == EventComputeSP)
            {
                Store4(0xC, segment);
                Store4(0x20, 0);
                Store4(0x24, 0);
            }
            if (eventId != EventStart) R[28] = PC;
            SetPC(0x1000);
            WD = 0x7FFFFFFF;
            EventProcessing = true;
            EventCurrent = eventId;
        }

        // ------------------------------------------------------------ loader (loader.c)

        /// <summary>Known BD+ content code generations (loader.c).</summary>
        private static readonly int[,] GenTable =
        {
            { 2007, 6, 8 }, { 2007, 12, 20 }, { 2008, 4, 14 }, { 2008, 8, 22 }, { 2009, 2, 2 }, { 2009, 3, 31 },
            { 2009, 8, 18 }, { 2010, 3, 6 }, { 2010, 9, 3 }, { 2011, 2, 28 }, { 2011, 6, 1 }, { 2011, 11, 16 },
            { 2011, 11, 16 }, { 2012, 8, 7 }, { 2013, 1, 25 }, { 2013, 4, 30 },
        };

        /// <summary>Loads BDSVM/00000.svm (whole file in 'svm'). Returns null on success, else the reason.</summary>
        public string LoadSvm(byte[] svm, out int gen, out int date)
        {
            gen = 0;
            date = 0;
            if (svm == null || svm.Length < 0x18) return "header missing";
            int year = (svm[0x0d] << 8) | svm[0x0e], month = svm[0x0f], day = svm[0x10];
            for (gen = 0; gen < GenTable.GetLength(0); gen++)
            {
                if (year < GenTable[gen, 0] ||
                    (year == GenTable[gen, 0] && month < GenTable[gen, 1]) ||
                    (year == GenTable[gen, 0] && month == GenTable[gen, 1] && day < GenTable[gen, 2]))
                    break;
            }
            date = (svm[0x0d] << 24) | (svm[0x0e] << 16) | (svm[0x0f] << 8) | svm[0x10];
            uint len = ((uint)svm[0x14] << 24) | ((uint)svm[0x15] << 16) | ((uint)svm[0x16] << 8) | svm[0x17];
            if (len >= MemorySize) return "section too long (" + len + ")";
            if (svm.Length - 0x18 < len) return "file shorter than its section";
            Buffer.BlockCopy(svm, 0x18, Mem, 0, (int)len);
            Array.Clear(Mem, 0, 0x1000);
            return null;
        }

        // ------------------------------------------------------------ execution (dlx_run)

        private static uint D(uint i) { return ((i >> 0x13) & 0x7C) >> 2; }
        private static uint S1(uint i) { return ((i >> 0x0E) & 0x7C) >> 2; }
        private static uint S2(uint i) { return ((i >> 0x09) & 0x7C) >> 2; }
        private static uint UI(uint i) { return i & 0xFFFF; }
        private static int SI(uint i) { return (short)(i & 0xFFFF); }
        private static uint UJ(uint i) { return i & 0x3FFFFFF; }
        private static int SJ(uint i) { return (i & 0x2000000) != 0 ? (int)((i & 0x3ffffff) | 0xFC000000) : (int)(i & 0x3FFFFFF); }

        /// <summary>
        /// Runs until the next TRAP (1), BREAK (2: watchdog expired) or an unknown opcode (-1). Only the
        /// "step until trap" mode of libbdplus is needed.
        /// </summary>
        public int Run()
        {
            Trap = 0;
            var m = Mem;
            var r = R;
            while (true)
            {
                PC &= Mask4;
                uint I = ((uint)m[PC] << 24) | ((uint)m[PC + 1] << 16) | ((uint)m[PC + 2] << 8) | m[PC + 3];
                I ^= IF;
                PC = (PC + 4) & Mask4;
                NumInstructions++;
                uint C = I >> 0x1A;
                uint d, s1, s2, a;
                switch (C)
                {
                    case 0x00: case 0x3B: case 0x3C: case 0x3D: case 0x3E: case 0x3F: // NOP
                        WD -= 1; break;
                    case 0x01: r[D(I)] = r[S1(I)] + r[S2(I)]; WD -= 1; break;                          // ADD
                    case 0x02: r[D(I)] = r[S1(I)] - r[S2(I)]; WD -= 1; break;                          // SUB
                    case 0x03: r[D(I)] = r[S1(I)] * r[S2(I)]; WD -= 4; break;                          // MUL
                    case 0x04:                                                                         // DIV
                        d = D(I); s1 = S1(I); s2 = S2(I);
                        if (r[s2] != 0)
                        {
                            int x = (int)r[s1], y = (int)r[s2];
                            r[d] = (x == int.MinValue && y == -1) ? (uint)x : (uint)(x / y);
                        }
                        else r[d] = 0;
                        WD -= 16; break;
                    case 0x05:                                                                         // DIVU
                        d = D(I); s1 = S1(I); s2 = S2(I);
                        r[d] = r[s2] != 0 ? r[s1] / r[s2] : 0;
                        WD -= 16; break;
                    case 0x06: r[D(I)] = r[S1(I)] << (int)(r[S2(I)] & 0x1F); WD -= 1; break;           // SLL
                    case 0x07: r[D(I)] = r[S1(I)] >> (int)(r[S2(I)] & 0x1F); WD -= 1; break;           // SRL
                    case 0x08: r[D(I)] = (uint)((int)r[S1(I)] >> (int)(r[S2(I)] & 0x1F)); WD -= 1; break; // SRA
                    case 0x09: r[D(I)] = r[S1(I)] & r[S2(I)]; WD -= 1; break;                          // AND
                    case 0x0A: r[D(I)] = r[S1(I)] | r[S2(I)]; WD -= 1; break;                          // OR
                    case 0x0B: r[D(I)] = r[S1(I)] ^ r[S2(I)]; WD -= 1; break;                          // XOR
                    case 0x0C: r[D(I)] = r[S1(I)] == r[S2(I)] ? 1u : 0u; WD -= 1; break;              // SEQ
                    case 0x0D: r[D(I)] = r[S1(I)] != r[S2(I)] ? 1u : 0u; WD -= 1; break;              // SNE
                    case 0x0E: r[D(I)] = (int)r[S1(I)] < (int)r[S2(I)] ? 1u : 0u; WD -= 1; break;     // SLT
                    case 0x0F: r[D(I)] = r[S1(I)] < r[S2(I)] ? 1u : 0u; WD -= 1; break;               // SBT
                    case 0x10: r[D(I)] = (int)r[S1(I)] > (int)r[S2(I)] ? 1u : 0u; WD -= 1; break;     // SGT
                    case 0x11: r[D(I)] = r[S1(I)] > r[S2(I)] ? 1u : 0u; WD -= 1; break;               // SAT
                    case 0x12: r[D(I)] = (int)r[S1(I)] <= (int)r[S2(I)] ? 1u : 0u; WD -= 1; break;    // SLE
                    case 0x13: r[D(I)] = r[S1(I)] <= r[S2(I)] ? 1u : 0u; WD -= 1; break;              // SBE
                    case 0x14: r[D(I)] = (int)r[S1(I)] >= (int)r[S2(I)] ? 1u : 0u; WD -= 1; break;    // SGE
                    case 0x15: r[D(I)] = r[S1(I)] >= r[S2(I)] ? 1u : 0u; WD -= 1; break;              // SAE
                    case 0x16: PC = r[S1(I)] & Mask4; WD -= 2; break;                                  // JR
                    case 0x17:                                                                         // JALR
                        // R31 is written first, as in libbdplus: "JALR R31" jumps to the return address.
                        r[31] = PC;
                        PC = r[S1(I)] & Mask4;
                        WD -= 3; break;
                    case 0x18: r[D(I)] = r[S1(I)] + (uint)SI(I); WD -= 1; break;                      // ADDIE
                    case 0x19: r[D(I)] = r[S1(I)] + UI(I); WD -= 1; break;                            // ADDI
                    case 0x1A: r[D(I)] = r[S1(I)] - (uint)SI(I); WD -= 1; break;                      // SUBIE
                    case 0x1B: r[D(I)] = r[S1(I)] - UI(I); WD -= 1; break;                            // SUBI
                    case 0x1C: r[D(I)] = r[S1(I)] << (int)(UI(I) & 0x1F); WD -= 1; break;             // SLLI
                    case 0x1D: r[D(I)] = r[S1(I)] >> (int)(UI(I) & 0x1F); WD -= 1; break;             // SRLI
                    case 0x1E: r[D(I)] = (uint)((int)r[S1(I)] >> (int)(UI(I) & 0x1F)); WD -= 1; break; // SRAI
                    case 0x1F: r[D(I)] = r[S1(I)] & UI(I); WD -= 1; break;                            // ANDI
                    case 0x20: r[D(I)] = r[S1(I)] | UI(I); WD -= 1; break;                            // ORI
                    case 0x21: r[D(I)] = r[S1(I)] ^ UI(I); WD -= 1; break;                            // XORI
                    case 0x22: r[D(I)] = r[S1(I)] == (uint)SI(I) ? 1u : 0u; WD -= 1; break;           // SEQI
                    case 0x23: r[D(I)] = r[S1(I)] != (uint)SI(I) ? 1u : 0u; WD -= 1; break;           // SNEI
                    case 0x24: r[D(I)] = (int)r[S1(I)] < SI(I) ? 1u : 0u; WD -= 1; break;             // SLI
                    case 0x25: r[D(I)] = r[S1(I)] < UI(I) ? 1u : 0u; WD -= 1; break;                  // SBI
                    case 0x26: r[D(I)] = (int)r[S1(I)] > SI(I) ? 1u : 0u; WD -= 1; break;             // SGI
                    case 0x27: r[D(I)] = r[S1(I)] > UI(I) ? 1u : 0u; WD -= 1; break;                  // SAI
                    case 0x28: r[D(I)] = (int)r[S1(I)] <= SI(I) ? 1u : 0u; WD -= 1; break;            // SLEI
                    case 0x29: r[D(I)] = r[S1(I)] <= UI(I) ? 1u : 0u; WD -= 1; break;                 // SBEI
                    case 0x2A: r[D(I)] = (int)r[S1(I)] >= SI(I) ? 1u : 0u; WD -= 1; break;            // SGEI
                    case 0x2B: r[D(I)] = r[S1(I)] >= UI(I) ? 1u : 0u; WD -= 1; break;                 // SAEI
                    case 0x2C: PC = (uint)(PC + SJ(I)) & Mask4; WD -= 2; break;                       // J
                    case 0x2D:                                                                         // JAL
                        r[31] = PC;
                        PC = (uint)(PC + SJ(I)) & Mask4;
                        WD -= 3; break;
                    case 0x2E: PC = (uint)(PC + (r[S1(I)] == 0 ? SI(I) : 0)) & Mask4; WD -= 2; break; // BEQZ
                    case 0x2F: PC = (uint)(PC + (r[S1(I)] != 0 ? SI(I) : 0)) & Mask4; WD -= 2; break; // BNEZ
                    case 0x30: r[D(I)] = UI(I) << 16; WD -= 1; break;                                 // LHI
                    case 0x31:                                                                         // LBE
                        a = (r[S1(I)] + (uint)SI(I)) & Mask1;
                        r[D(I)] = (uint)(sbyte)m[a];
                        WD -= 4; break;
                    case 0x32:                                                                         // LB
                        a = (r[S1(I)] + (uint)SI(I)) & Mask1;
                        r[D(I)] = m[a];
                        WD -= 4; break;
                    case 0x33:                                                                         // LWE
                        a = (r[S1(I)] + (uint)SI(I)) & Mask2;
                        r[D(I)] = (uint)(short)((m[a] << 8) | m[a + 1]);
                        WD -= 4; break;
                    case 0x34:                                                                         // LW
                        a = (r[S1(I)] + (uint)SI(I)) & Mask2;
                        r[D(I)] = (uint)((m[a] << 8) | m[a + 1]);
                        WD -= 4; break;
                    case 0x35:                                                                         // LDW
                        a = (r[S1(I)] + (uint)SI(I)) & Mask4;
                        r[D(I)] = ((uint)m[a] << 24) | ((uint)m[a + 1] << 16) | ((uint)m[a + 2] << 8) | m[a + 3];
                        WD -= 2; break;
                    case 0x36:                                                                         // SB
                        a = (r[S1(I)] + (uint)SI(I)) & Mask1;
                        m[a] = (byte)r[D(I)];
                        WD -= 4; break;
                    case 0x37:                                                                         // SW
                        a = (r[S1(I)] + (uint)SI(I)) & Mask2;
                        d = r[D(I)];
                        m[a] = (byte)(d >> 8);
                        m[a + 1] = (byte)d;
                        WD -= 4; break;
                    case 0x38:                                                                         // SDW
                        a = (r[S1(I)] + (uint)SI(I)) & Mask4;
                        d = r[D(I)];
                        m[a] = (byte)(d >> 24);
                        m[a + 1] = (byte)(d >> 16);
                        m[a + 2] = (byte)(d >> 8);
                        m[a + 3] = (byte)d;
                        WD -= 2; break;
                    case 0x39:                                                                         // TRAP
                        Trap = UJ(I);
                        if (EventProcessing) WD = 0x7FFFFFFF;
                        if (OnTrap != null) OnTrap(this, Trap);
                        NumTraps++;
                        if (WD <= 0 && !EventProcessing) WD = 0xFA0;
                        return 1;
                    case 0x3A: IF = r[S1(I)]; WD -= 6; break;                                         // INSTF
                    default:
                        return -1;
                }
                r[0] = 0;
                if (WD <= 0)
                {
                    NumBreaks++;
                    if (EventProcessing) R[28] = PC;
                    WD = 0xFA0;
                    return 2;
                }
            }
        }
    }
}
