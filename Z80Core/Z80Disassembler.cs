using System;

namespace Z80Core;

/// <summary>
/// Single-instruction Z80 disassembler. Reads bytes from any
/// <see cref="IMemory"/> starting at a given address and returns the
/// formatted mnemonic plus the number of bytes consumed.
///
/// Decoding follows the standard x/y/z form (x = top 2 bits, y = bits 5-3,
/// z = bottom 3 bits, p = y &gt;&gt; 1, q = y &amp; 1) so most of the decoder
/// is table-driven instead of a 256-entry switch — mirrors how the CPU
/// executor in <c>Z80Main.cs</c> is structured.
///
/// Callers can pass an <c>isSideEffectAddr</c> predicate to mark addresses
/// whose reads must not be performed by the disassembler (e.g. memory-mapped
/// I/O ports that latch counters or scan keyboards on read). Bytes at those
/// addresses are reported as zero instead of being read through.
/// </summary>
public static class Z80Disassembler
{
    public readonly struct Result
    {
        public readonly string Text;
        public readonly int Length;
        public Result(string text, int length) { Text = text; Length = length; }
    }

    private static readonly string[] R   = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
    private static readonly string[] RP  = { "BC", "DE", "HL", "SP" };
    private static readonly string[] RP2 = { "BC", "DE", "HL", "AF" };
    private static readonly string[] CC  = { "NZ", "Z", "NC", "C", "PO", "PE", "P", "M" };
    private static readonly string[] ALU = { "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP " };
    private static readonly string[] ROT = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };
    private static readonly string[] BLI = {
        "LDI",  "CPI",  "INI",  "OUTI",
        "LDD",  "CPD",  "IND",  "OUTD",
        "LDIR", "CPIR", "INIR", "OTIR",
        "LDDR", "CPDR", "INDR", "OTDR",
    };

    public static Result Disassemble(IMemory mem, ushort addr, Func<ushort, bool>? isSideEffectAddr = null)
    {
        var r = new Reader(mem, addr, isSideEffectAddr);
        int idx = 0;
        byte op = r.FetchByte();
        while (op == 0xDD || op == 0xFD)
        {
            idx = (op == 0xDD) ? 1 : 2;
            op = r.FetchByte();
        }

        string text;
        if (op == 0xCB)
        {
            if (idx == 0)
            {
                text = DecodeCB(r);
            }
            else
            {
                sbyte d = r.FetchSByte();
                byte cbOp = r.FetchByte();
                text = DecodeIndexCB(idx, d, cbOp);
            }
        }
        else if (op == 0xED && idx == 0)
        {
            text = DecodeED(r);
        }
        else if (op == 0xED)
        {
            // DD/FD ED is undefined — DD/FD is ignored, ED is executed normally.
            text = DecodeED(r);
        }
        else
        {
            text = DecodeMain(r, op, idx);
        }

        int len = (r.Cursor - addr) & 0xFFFF;
        if (len <= 0) len = 1;
        return new Result(text, len);
    }

    // --- Main (unprefixed, or after DD/FD) ----------------------------------

    private static string DecodeMain(Reader r, byte op, int idx)
    {
        int x = op >> 6;
        int y = (op >> 3) & 7;
        int z = op & 7;
        int p = y >> 1;
        int q = y & 1;

        string idxName = idx == 0 ? "HL" : (idx == 1 ? "IX" : "IY");

        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0:
                        switch (y)
                        {
                            case 0: return "NOP";
                            case 1: return "EX AF,AF'";
                            case 2: { sbyte d = r.FetchSByte(); return $"DJNZ ${(ushort)(r.Cursor + d):X4}"; }
                            case 3: { sbyte d = r.FetchSByte(); return $"JR ${(ushort)(r.Cursor + d):X4}"; }
                            default: { sbyte d = r.FetchSByte(); return $"JR {CC[y - 4]},${(ushort)(r.Cursor + d):X4}"; }
                        }
                    case 1:
                        if (q == 0) return $"LD {RpName(p, idx)},${r.FetchWord():X4}";
                        return $"ADD {idxName},{RpName(p, idx)}";
                    case 2:
                        switch (y)
                        {
                            case 0: return "LD (BC),A";
                            case 1: return "LD A,(BC)";
                            case 2: return "LD (DE),A";
                            case 3: return "LD A,(DE)";
                            case 4: return $"LD (${r.FetchWord():X4}),{idxName}";
                            case 5: return $"LD {idxName},(${r.FetchWord():X4})";
                            case 6: return $"LD (${r.FetchWord():X4}),A";
                            case 7: return $"LD A,(${r.FetchWord():X4})";
                        }
                        break;
                    case 3:
                        return q == 0 ? $"INC {RpName(p, idx)}" : $"DEC {RpName(p, idx)}";
                    case 4:
                        return $"INC {RegName(r, y, idx)}";
                    case 5:
                        return $"DEC {RegName(r, y, idx)}";
                    case 6:
                        {
                            // LD r,n  — for (IX+d) the d comes before n.
                            string dst = RegName(r, y, idx);
                            return $"LD {dst},${r.FetchByte():X2}";
                        }
                    case 7:
                        return y switch
                        {
                            0 => "RLCA",
                            1 => "RRCA",
                            2 => "RLA",
                            3 => "RRA",
                            4 => "DAA",
                            5 => "CPL",
                            6 => "SCF",
                            7 => "CCF",
                            _ => "?"
                        };
                }
                break;

            case 1:
                if (y == 6 && z == 6) return "HALT";
                {
                    // LD r[y],r[z]. With DD/FD, when exactly one side is (HL),
                    // that side becomes (IX+d)/(IY+d) and the OTHER side stays
                    // as the plain Z80 reg (H/L, not IXH/IXL). When neither is
                    // (HL), DD/FD remaps H→IXH and L→IXL on both sides.
                    bool hasIndirect = (y == 6 || z == 6);
                    string dst, src;
                    if (idx != 0 && hasIndirect)
                    {
                        sbyte d = r.FetchSByte();
                        string indexed = idx == 1 ? $"(IX{Disp(d)})" : $"(IY{Disp(d)})";
                        dst = (y == 6) ? indexed : R[y];
                        src = (z == 6) ? indexed : R[z];
                    }
                    else
                    {
                        dst = RegName(r, y, idx);
                        src = RegName(r, z, idx);
                    }
                    return $"LD {dst},{src}";
                }

            case 2:
                return $"{ALU[y]}{RegName(r, z, idx)}";

            case 3:
                switch (z)
                {
                    case 0: return $"RET {CC[y]}";
                    case 1:
                        if (q == 0)
                        {
                            string name = (p == 2 && idx != 0) ? idxName : RP2[p];
                            return $"POP {name}";
                        }
                        return p switch
                        {
                            0 => "RET",
                            1 => "EXX",
                            2 => idx == 0 ? "JP (HL)" : (idx == 1 ? "JP (IX)" : "JP (IY)"),
                            3 => idx == 0 ? "LD SP,HL" : (idx == 1 ? "LD SP,IX" : "LD SP,IY"),
                            _ => "?"
                        };
                    case 2: return $"JP {CC[y]},${r.FetchWord():X4}";
                    case 3:
                        switch (y)
                        {
                            case 0: return $"JP ${r.FetchWord():X4}";
                            case 1: return "CB?";   // CB is handled in caller
                            case 2: return $"OUT (${r.FetchByte():X2}),A";
                            case 3: return $"IN A,(${r.FetchByte():X2})";
                            case 4: return idx == 0 ? "EX (SP),HL" : (idx == 1 ? "EX (SP),IX" : "EX (SP),IY");
                            case 5: return "EX DE,HL";
                            case 6: return "DI";
                            case 7: return "EI";
                        }
                        break;
                    case 4: return $"CALL {CC[y]},${r.FetchWord():X4}";
                    case 5:
                        if (q == 0)
                        {
                            string name = (p == 2 && idx != 0) ? idxName : RP2[p];
                            return $"PUSH {name}";
                        }
                        return p switch
                        {
                            0 => $"CALL ${r.FetchWord():X4}",
                            _ => "?prefix?"        // DD/ED/FD prefixes handled in caller
                        };
                    case 6: return $"{ALU[y]}${r.FetchByte():X2}";
                    case 7: return $"RST ${y * 8:X2}";
                }
                break;
        }
        return $"DB ${op:X2}";
    }

    private static string DecodeCB(Reader r)
    {
        byte op = r.FetchByte();
        int x = op >> 6;
        int y = (op >> 3) & 7;
        int z = op & 7;
        string reg = R[z];
        return x switch
        {
            0 => $"{ROT[y]} {reg}",
            1 => $"BIT {y},{reg}",
            2 => $"RES {y},{reg}",
            3 => $"SET {y},{reg}",
            _ => "?"
        };
    }

    private static string DecodeIndexCB(int idx, sbyte d, byte op)
    {
        int x = op >> 6;
        int y = (op >> 3) & 7;
        int z = op & 7;
        string indexed = idx == 1 ? $"(IX{Disp(d)})" : $"(IY{Disp(d)})";
        // Undocumented: for x=0/2/3 with z != 6, the operation also stores
        // the result in r[z]. BIT (x=1) has no side effect so we just show
        // the indexed operand.
        string suffix = (z != 6 && x != 1) ? $",{R[z]}" : "";
        return x switch
        {
            0 => $"{ROT[y]} {indexed}{suffix}",
            1 => $"BIT {y},{indexed}",
            2 => $"RES {y},{indexed}{suffix}",
            3 => $"SET {y},{indexed}{suffix}",
            _ => "?"
        };
    }

    private static string DecodeED(Reader r)
    {
        byte op = r.FetchByte();
        int x = op >> 6;
        int y = (op >> 3) & 7;
        int z = op & 7;
        int p = y >> 1;
        int q = y & 1;

        if (x == 1)
        {
            switch (z)
            {
                case 0: return y == 6 ? "IN (C)" : $"IN {R[y]},(C)";
                case 1: return y == 6 ? "OUT (C),0" : $"OUT (C),{R[y]}";
                case 2: return q == 0 ? $"SBC HL,{RP[p]}" : $"ADC HL,{RP[p]}";
                case 3:
                    return q == 0
                        ? $"LD (${r.FetchWord():X4}),{RP[p]}"
                        : $"LD {RP[p]},(${r.FetchWord():X4})";
                case 4: return "NEG";
                case 5: return y == 1 ? "RETI" : "RETN";
                case 6:
                    return y switch
                    {
                        0 or 4 => "IM 0",
                        1 or 5 => "IM 0/1",
                        2 or 6 => "IM 1",
                        3 or 7 => "IM 2",
                        _ => "IM ?"
                    };
                case 7:
                    return y switch
                    {
                        0 => "LD I,A",
                        1 => "LD R,A",
                        2 => "LD A,I",
                        3 => "LD A,R",
                        4 => "RRD",
                        5 => "RLD",
                        _ => "NOP"
                    };
            }
        }
        if (x == 2 && y >= 4 && z <= 3)
        {
            int i = (y - 4) * 4 + z;
            if (i >= 0 && i < BLI.Length) return BLI[i];
        }
        return $"DB $ED,${op:X2}";
    }

    // --- Helpers ------------------------------------------------------------

    private static string RegName(Reader r, int rid, int idx)
    {
        if (rid == 6)
        {
            if (idx == 0) return "(HL)";
            sbyte d = r.FetchSByte();
            return idx == 1 ? $"(IX{Disp(d)})" : $"(IY{Disp(d)})";
        }
        if ((rid == 4 || rid == 5) && idx != 0)
        {
            if (idx == 1) return rid == 4 ? "IXH" : "IXL";
            return rid == 4 ? "IYH" : "IYL";
        }
        return R[rid];
    }

    private static string RpName(int p, int idx)
    {
        if (p == 2 && idx != 0) return idx == 1 ? "IX" : "IY";
        return RP[p];
    }

    private static string Disp(sbyte d) => d >= 0 ? $"+{d}" : $"{d}";

    private sealed class Reader
    {
        private readonly IMemory _mem;
        private readonly Func<ushort, bool>? _isSideEffectAddr;
        public ushort Cursor;
        public Reader(IMemory mem, ushort start, Func<ushort, bool>? isSideEffectAddr)
        {
            _mem = mem;
            _isSideEffectAddr = isSideEffectAddr;
            Cursor = start;
        }

        public byte FetchByte()
        {
            byte v = (_isSideEffectAddr != null && _isSideEffectAddr(Cursor)) ? (byte)0 : _mem.Read(Cursor);
            Cursor = (ushort)(Cursor + 1);
            return v;
        }

        public ushort FetchWord()
        {
            byte lo = FetchByte();
            byte hi = FetchByte();
            return (ushort)(lo | (hi << 8));
        }

        public sbyte FetchSByte() => (sbyte)FetchByte();
    }
}
