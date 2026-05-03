using System;

namespace MZ700Emul.Z80;

public sealed partial class Z80Cpu
{
    // CB prefix: rotates / shifts / bit / res / set on registers or (HL)
    private int ExecuteCBPrefix()
    {
        byte op = Fetch();
        IncR();

        int x = (op >> 6) & 3;
        int y = (op >> 3) & 7;
        int z = op & 7;

        // Read register
        byte v;
        int cyc = 8;
        ushort addr = 0;
        bool isMem = z == 6;
        if (isMem)
        {
            addr = HL;
            v = Mem.Read(addr);
            cyc = 15;
        }
        else
        {
            v = GetRegDirect(z);
        }

        switch (x)
        {
            case 0: // rot/shift
                v = y switch
                {
                    0 => Rlc(v),
                    1 => Rrc(v),
                    2 => Rl(v),
                    3 => Rr(v),
                    4 => Sla(v),
                    5 => Sra(v),
                    6 => Sll(v),
                    7 => Srl(v),
                    _ => v
                };
                if (isMem) { Mem.Write(addr, v); }
                else { SetRegDirect(z, v); }
                return cyc;
            case 1: // BIT y,r
                Bit(y, v, isMem ? (byte)(WZ >> 8) : v);
                return isMem ? 12 : 8;
            case 2: // RES y,r
                v = (byte)(v & ~(1 << y));
                if (isMem) Mem.Write(addr, v); else SetRegDirect(z, v);
                return cyc;
            case 3: // SET y,r
                v = (byte)(v | (1 << y));
                if (isMem) Mem.Write(addr, v); else SetRegDirect(z, v);
                return cyc;
        }
        return cyc;
    }

    // Access registers by 3-bit code ignoring DD/FD substitution (for CB ops on plain regs)
    private byte GetRegDirect(int r) => r switch
    {
        0 => B,
        1 => C,
        2 => D,
        3 => E,
        4 => H,
        5 => L,
        6 => Mem.Read(HL),
        7 => A,
        _ => 0
    };

    private void SetRegDirect(int r, byte v)
    {
        switch (r)
        {
            case 0: B = v; break;
            case 1: C = v; break;
            case 2: D = v; break;
            case 3: E = v; break;
            case 4: H = v; break;
            case 5: L = v; break;
            case 6: Mem.Write(HL, v); break;
            case 7: A = v; break;
        }
    }

    // DD CB d op / FD CB d op : bit ops on (IX+d) or (IY+d)
    // The op byte encodes which bit operation AND which "alternate" register
    // receives a copy of the result (for undocumented variants). For register 6
    // (HL slot) the operation targets only (IX+d). For other reg codes, the
    // result is also written to the named register (LD r,RLC(IX+d)-style).
    private int ExecuteDDCB(int idxSel)
    {
        sbyte d = (sbyte)Fetch();
        byte op = Fetch();
        // NOTE: R is NOT incremented for the op byte after DD CB d (only the first two fetches count)

        int x = (op >> 6) & 3;
        int y = (op >> 3) & 7;
        int z = op & 7;

        ushort baseR = idxSel == 1 ? IX : IY;
        ushort addr = (ushort)(baseR + d);
        WZ = addr;
        byte v = Mem.Read(addr);
        byte result = v;

        switch (x)
        {
            case 0:
                result = y switch
                {
                    0 => Rlc(v),
                    1 => Rrc(v),
                    2 => Rl(v),
                    3 => Rr(v),
                    4 => Sla(v),
                    5 => Sra(v),
                    6 => Sll(v),
                    7 => Srl(v),
                    _ => v
                };
                Mem.Write(addr, result);
                if (z != 6) SetRegDirect(z, result);
                return 23;
            case 1: // BIT y,(IX+d) - z is ignored
                Bit(y, v, (byte)(addr >> 8));
                return 20;
            case 2: // RES y,(IX+d)
                result = (byte)(v & ~(1 << y));
                Mem.Write(addr, result);
                if (z != 6) SetRegDirect(z, result);
                return 23;
            case 3: // SET y,(IX+d)
                result = (byte)(v | (1 << y));
                Mem.Write(addr, result);
                if (z != 6) SetRegDirect(z, result);
                return 23;
        }
        return 23;
    }
}
