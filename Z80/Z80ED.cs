using System;

namespace MZ700Emul.Z80;

public sealed partial class Z80Cpu
{
    private int ExecuteEDPrefix()
    {
        byte op = Fetch();
        IncR();

        int x = (op >> 6) & 3;
        int y = (op >> 3) & 7;
        int z = op & 7;
        int p = y >> 1;
        int q = y & 1;

        // x=0 and x=3 are mostly NOPs (invalid)
        if (x == 0 || x == 3) return 8;

        if (x == 1)
        {
            switch (z)
            {
                case 0: // IN r,(C) / IN (C)
                {
                    byte v = Io.In(BC);
                    F = (byte)((F & FLAG_C) | (v & (FLAG_S | FLAG_Y | FLAG_X)) | (v == 0 ? FLAG_Z : 0) | ParityTable[v]);
                    if (y != 6) SetRegDirect(y, v);
                    return 12;
                }
                case 1: // OUT (C),r / OUT (C),0
                {
                    byte v = (y == 6) ? (byte)0 : GetRegDirect(y);
                    Io.Out(BC, v);
                    return 12;
                }
                case 2: // SBC / ADC HL,rp
                {
                    ushort other = p switch
                    {
                        0 => BC,
                        1 => DE,
                        2 => HL,
                        3 => SP,
                        _ => (ushort)0
                    };
                    if (q == 0) HL = Sbc16(HL, other); else HL = Adc16(HL, other);
                    return 15;
                }
                case 3: // LD (nn),rp / LD rp,(nn)
                {
                    ushort nn = FetchWord();
                    if (q == 0)
                    {
                        ushort v = p switch
                        {
                            0 => BC,
                            1 => DE,
                            2 => HL,
                            3 => SP,
                            _ => (ushort)0
                        };
                        WriteWord(nn, v);
                    }
                    else
                    {
                        ushort v = ReadWord(nn);
                        switch (p)
                        {
                            case 0: BC = v; break;
                            case 1: DE = v; break;
                            case 2: HL = v; break;
                            case 3: SP = v; break;
                        }
                    }
                    return 20;
                }
                case 4: // NEG
                {
                    byte a = A;
                    A = Sub8(0, a, 0);
                    return 8;
                }
                case 5: // RETN / RETI
                    PC = Pop();
                    IFF1 = IFF2;
                    return 14;
                case 6: // IM
                    IM = y switch
                    {
                        0 or 4 => 0,
                        1 or 5 => 0,
                        2 or 6 => 1,
                        3 or 7 => 2,
                        _ => 0
                    };
                    return 8;
                case 7:
                    switch (y)
                    {
                        case 0: I = A; return 9;                         // LD I,A
                        case 1: R = A; return 9;                         // LD R,A
                        case 2: // LD A,I
                        {
                            A = I;
                            F = (byte)((F & FLAG_C) | (A & (FLAG_S | FLAG_Y | FLAG_X)) | (A == 0 ? FLAG_Z : 0) | (IFF2 ? FLAG_PV : 0));
                            return 9;
                        }
                        case 3: // LD A,R
                        {
                            A = R;
                            F = (byte)((F & FLAG_C) | (A & (FLAG_S | FLAG_Y | FLAG_X)) | (A == 0 ? FLAG_Z : 0) | (IFF2 ? FLAG_PV : 0));
                            return 9;
                        }
                        case 4: // RRD
                        {
                            byte m = Mem.Read(HL);
                            byte newM = (byte)((A << 4) | (m >> 4));
                            A = (byte)((A & 0xF0) | (m & 0x0F));
                            Mem.Write(HL, newM);
                            F = (byte)((F & FLAG_C) | (A & (FLAG_S | FLAG_Y | FLAG_X)) | (A == 0 ? FLAG_Z : 0) | ParityTable[A]);
                            return 18;
                        }
                        case 5: // RLD
                        {
                            byte m = Mem.Read(HL);
                            byte newM = (byte)((m << 4) | (A & 0x0F));
                            A = (byte)((A & 0xF0) | (m >> 4));
                            Mem.Write(HL, newM);
                            F = (byte)((F & FLAG_C) | (A & (FLAG_S | FLAG_Y | FLAG_X)) | (A == 0 ? FLAG_Z : 0) | ParityTable[A]);
                            return 18;
                        }
                        case 6: case 7: return 8; // NOP
                    }
                    return 8;
            }
            return 8;
        }

        // x == 2: block instructions
        // z=0 LDI/LDD/LDIR/LDDR (y=4/5/6/7)
        // z=1 CPI/CPD/CPIR/CPDR
        // z=2 INI/IND/INIR/INDR
        // z=3 OUTI/OUTD/OTIR/OTDR
        if (y < 4) return 8;
        return ExecuteBlockOp(y, z);
    }

    private int ExecuteBlockOp(int y, int z)
    {
        bool repeat = (y & 2) != 0;
        bool decr = (y & 1) != 0;
        int dir = decr ? -1 : 1;

        switch (z)
        {
            case 0: // LDI/LDD/LDIR/LDDR
            {
                byte v = Mem.Read(HL);
                Mem.Write(DE, v);
                HL = (ushort)(HL + dir);
                DE = (ushort)(DE + dir);
                BC = (ushort)(BC - 1);
                byte n = (byte)(v + A);
                F = (byte)(
                    (F & (FLAG_S | FLAG_Z | FLAG_C)) |
                    ((n & 0x02) != 0 ? FLAG_Y : 0) |
                    ((n & 0x08) != 0 ? FLAG_X : 0) |
                    (BC != 0 ? FLAG_PV : 0)
                );
                if (repeat && BC != 0)
                {
                    PC -= 2;
                    return 21;
                }
                return 16;
            }
            case 1: // CPI/CPD/CPIR/CPDR
            {
                byte v = Mem.Read(HL);
                byte r = (byte)(A - v);
                HL = (ushort)(HL + dir);
                BC = (ushort)(BC - 1);
                byte hf = (byte)(((A ^ v ^ r) & 0x10) != 0 ? FLAG_H : 0);
                byte n = (byte)(r - (hf != 0 ? 1 : 0));
                F = (byte)(
                    (F & FLAG_C) |
                    FLAG_N |
                    hf |
                    (r & FLAG_S) |
                    (r == 0 ? FLAG_Z : 0) |
                    ((n & 0x02) != 0 ? FLAG_Y : 0) |
                    ((n & 0x08) != 0 ? FLAG_X : 0) |
                    (BC != 0 ? FLAG_PV : 0)
                );
                if (repeat && BC != 0 && r != 0)
                {
                    PC -= 2;
                    return 21;
                }
                return 16;
            }
            case 2: // INI/IND/INIR/INDR
            {
                byte v = Io.In(BC);
                Mem.Write(HL, v);
                B = (byte)(B - 1);
                HL = (ushort)(HL + dir);
                F = (byte)(
                    (F & FLAG_C) |
                    (B == 0 ? FLAG_Z : 0) |
                    FLAG_N |
                    (B & (FLAG_S | FLAG_Y | FLAG_X))
                );
                if (repeat && B != 0) { PC -= 2; return 21; }
                return 16;
            }
            case 3: // OUTI/OUTD/OTIR/OTDR
            {
                byte v = Mem.Read(HL);
                B = (byte)(B - 1);
                Io.Out(BC, v);
                HL = (ushort)(HL + dir);
                F = (byte)(
                    (F & FLAG_C) |
                    (B == 0 ? FLAG_Z : 0) |
                    FLAG_N |
                    (B & (FLAG_S | FLAG_Y | FLAG_X))
                );
                if (repeat && B != 0) { PC -= 2; return 21; }
                return 16;
            }
        }
        return 8;
    }
}
