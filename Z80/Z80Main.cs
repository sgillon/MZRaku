using System;

namespace MZ700Emul.Z80;

public sealed partial class Z80Cpu
{
    // Index register mode: 0 = none (HL), 1 = IX, 2 = IY
    private int _idx = 0;

    // Read/write register by 3-bit encoding: 0=B 1=C 2=D 3=E 4=H 5=L 6=(HL) 7=A
    private byte GetR(int r, out int extraCycles)
    {
        extraCycles = 0;
        switch (r)
        {
            case 0: return B;
            case 1: return C;
            case 2: return D;
            case 3: return E;
            case 4: return _idx == 0 ? H : (_idx == 1 ? IXH : IYH);
            case 5: return _idx == 0 ? L : (_idx == 1 ? IXL : IYL);
            case 6:
            {
                ushort addr = GetHLorIdxWithDisp(out int c);
                extraCycles = c;
                return Mem.Read(addr);
            }
            case 7: return A;
        }
        return 0;
    }

    private void SetR(int r, byte v, out int extraCycles)
    {
        extraCycles = 0;
        switch (r)
        {
            case 0: B = v; break;
            case 1: C = v; break;
            case 2: D = v; break;
            case 3: E = v; break;
            case 4: if (_idx == 0) H = v; else if (_idx == 1) IXH = v; else IYH = v; break;
            case 5: if (_idx == 0) L = v; else if (_idx == 1) IXL = v; else IYL = v; break;
            case 6:
            {
                ushort addr = GetHLorIdxWithDisp(out int c);
                extraCycles = c;
                Mem.Write(addr, v);
                break;
            }
            case 7: A = v; break;
        }
    }

    // For LD r,r' where destination or source is (IX+d)/(IY+d), the displacement
    // is only consumed once. When both src and dst are H/L on a DD/FD, they
    // still refer to IXH/IXL etc., but that's the default behavior above.
    // When using (HL) in the middle of LD r,(HL) on DD/FD, the d comes after opcode.
    // Our Fetch ordering: opcode byte (already fetched), then if (HL) is involved,
    // the displacement d. We track this via _idx and read d here.
    private ushort GetHLorIdxWithDisp(out int extraCycles)
    {
        extraCycles = 0;
        if (_idx == 0) return HL;
        sbyte d = (sbyte)Fetch();
        extraCycles = 8;
        ushort baseR = _idx == 1 ? IX : IY;
        ushort eff = (ushort)(baseR + d);
        WZ = eff;
        return eff;
    }

    // Fetch a single displacement byte and resolve (IX+d) or (IY+d). The
    // caller guarantees _idx != 0. Used by read-modify-write opcodes that
    // touch the same (IX+d) cell twice — calling GetHLorIdxWithDisp twice
    // would consume two stream bytes as separate displacements.
    private ushort IdxAddrFetchD()
    {
        sbyte d = (sbyte)Fetch();
        ushort baseR = _idx == 1 ? IX : IY;
        ushort eff = (ushort)(baseR + d);
        WZ = eff;
        return eff;
    }

    // For opcodes where (HL) would be read but we're in IX/IY mode, when the
    // opcode does NOT encode register 6 (HL), then H/L refer to IXH/IXL. But
    // for LD (HL),n style where (HL) is encoded, we need to read 'd' first and
    // then the operand 'n'. The caller handles ordering.
    private bool IsHLAccess(int r) => r == 6;

    // Return the reg pair 00=BC 01=DE 10=HL/IX/IY 11=SP
    private ushort GetRP(int rp)
    {
        return rp switch
        {
            0 => BC,
            1 => DE,
            2 => (_idx == 0 ? HL : (_idx == 1 ? IX : IY)),
            3 => SP,
            _ => 0
        };
    }

    private void SetRP(int rp, ushort v)
    {
        switch (rp)
        {
            case 0: BC = v; break;
            case 1: DE = v; break;
            case 2: if (_idx == 0) HL = v; else if (_idx == 1) IX = v; else IY = v; break;
            case 3: SP = v; break;
        }
    }

    // Condition codes cc (3 bits)
    private bool Cond(int cc) => cc switch
    {
        0 => (F & FLAG_Z) == 0,   // NZ
        1 => (F & FLAG_Z) != 0,   // Z
        2 => (F & FLAG_C) == 0,   // NC
        3 => (F & FLAG_C) != 0,   // C
        4 => (F & FLAG_PV) == 0,  // PO
        5 => (F & FLAG_PV) != 0,  // PE
        6 => (F & FLAG_S) == 0,   // P
        7 => (F & FLAG_S) != 0,   // M
        _ => false
    };

    // Main opcode execution. 'op' is the first opcode byte (after any prefix).
    private int ExecuteMain(byte op)
    {
        // 00-3F: misc and 8-bit inc/dec and LD r,n
        // Group by high 2 bits
        int x = (op >> 6) & 3;
        int y = (op >> 3) & 7;
        int z = op & 7;
        int p = y >> 1;
        int q = y & 1;

        switch (x)
        {
            case 0: return ExecuteX0(op, y, z, p, q);
            case 1: return ExecuteX1(op, y, z);
            case 2: return ExecuteX2(op, y, z);
            case 3: return ExecuteX3(op, y, z, p, q);
        }
        return 4;
    }

    private int ExecuteX0(byte op, int y, int z, int p, int q)
    {
        int ex;
        switch (z)
        {
            case 0:
                switch (y)
                {
                    case 0: return 4; // NOP
                    case 1: // EX AF,AF'
                    {
                        byte t;
                        t = A; A = A_; A_ = t;
                        t = F; F = F_; F_ = t;
                        return 4;
                    }
                    case 2: // DJNZ d
                    {
                        sbyte d = (sbyte)Fetch();
                        B = (byte)(B - 1);
                        if (B != 0) { PC = (ushort)(PC + d); return 13; }
                        return 8;
                    }
                    case 3: // JR d
                    {
                        sbyte d = (sbyte)Fetch();
                        PC = (ushort)(PC + d);
                        return 12;
                    }
                    case 4: case 5: case 6: case 7: // JR cc[y-4],d
                    {
                        sbyte d = (sbyte)Fetch();
                        int cc = y - 4;
                        bool take = cc switch
                        {
                            0 => (F & FLAG_Z) == 0,
                            1 => (F & FLAG_Z) != 0,
                            2 => (F & FLAG_C) == 0,
                            3 => (F & FLAG_C) != 0,
                            _ => false
                        };
                        if (take) { PC = (ushort)(PC + d); return 12; }
                        return 7;
                    }
                }
                return 4;

            case 1:
                if (q == 0)
                {
                    ushort nn = FetchWord();
                    SetRP(p, nn);
                    return (p == 2 && _idx != 0) ? 14 : 10;
                }
                else
                {
                    // ADD HL,rp (or ADD IX,rp / ADD IY,rp)
                    ushort cur = _idx == 0 ? HL : (_idx == 1 ? IX : IY);
                    ushort other = p switch
                    {
                        0 => BC,
                        1 => DE,
                        2 => (_idx == 0 ? HL : (_idx == 1 ? IX : IY)),
                        3 => SP,
                        _ => 0
                    };
                    ushort r = Add16(cur, other);
                    if (_idx == 0) HL = r; else if (_idx == 1) IX = r; else IY = r;
                    return (_idx == 0) ? 11 : 15;
                }

            case 2:
                // LD (BC)/A, LD A/(BC), LD (DE)/A, LD A/(DE), LD (nn)/HL, LD HL/(nn), LD (nn)/A, LD A/(nn)
                switch (y)
                {
                    case 0: Mem.Write(BC, A); return 7;               // LD (BC),A
                    case 1: A = Mem.Read(BC); return 7;               // LD A,(BC)
                    case 2: Mem.Write(DE, A); return 7;               // LD (DE),A
                    case 3: A = Mem.Read(DE); return 7;               // LD A,(DE)
                    case 4:                                            // LD (nn),HL / IX / IY
                    {
                        ushort nn = FetchWord();
                        ushort v = _idx == 0 ? HL : (_idx == 1 ? IX : IY);
                        WriteWord(nn, v);
                        return (_idx == 0) ? 16 : 20;
                    }
                    case 5:
                    {
                        ushort nn = FetchWord();
                        ushort v = ReadWord(nn);
                        if (_idx == 0) HL = v; else if (_idx == 1) IX = v; else IY = v;
                        return (_idx == 0) ? 16 : 20;
                    }
                    case 6:                                            // LD (nn),A
                    {
                        ushort nn = FetchWord();
                        Mem.Write(nn, A);
                        return 13;
                    }
                    case 7:                                            // LD A,(nn)
                    {
                        ushort nn = FetchWord();
                        A = Mem.Read(nn);
                        return 13;
                    }
                }
                return 4;

            case 3:
                // INC/DEC rp
                if (q == 0)
                {
                    ushort v = p switch
                    {
                        0 => BC,
                        1 => DE,
                        2 => (_idx == 0 ? HL : (_idx == 1 ? IX : IY)),
                        3 => SP,
                        _ => (ushort)0
                    };
                    v = (ushort)(v + 1);
                    SetRP(p, v);
                    return (_idx == 0) ? 6 : 10;
                }
                else
                {
                    ushort v = p switch
                    {
                        0 => BC,
                        1 => DE,
                        2 => (_idx == 0 ? HL : (_idx == 1 ? IX : IY)),
                        3 => SP,
                        _ => (ushort)0
                    };
                    v = (ushort)(v - 1);
                    SetRP(p, v);
                    return (_idx == 0) ? 6 : 10;
                }

            case 4: // INC r
            {
                if (y == 6 && _idx != 0)
                {
                    // INC (IX+d) / INC (IY+d): displacement must be fetched
                    // ONCE — naive GetR(6)+SetR(6) would Fetch d twice (the
                    // second Fetch consumes the next instruction's opcode
                    // as a bogus displacement). 23 T-states on real Z80.
                    ushort addr = IdxAddrFetchD();
                    Mem.Write(addr, Inc8(Mem.Read(addr)));
                    return 23;
                }
                byte v = GetR(y, out ex);
                v = Inc8(v);
                SetR(y, v, out int ex2);
                if (y == 6) return 11 + ex + ex2; // (HL) inc
                return 4;
            }

            case 5: // DEC r
            {
                if (y == 6 && _idx != 0)
                {
                    ushort addr = IdxAddrFetchD();
                    Mem.Write(addr, Dec8(Mem.Read(addr)));
                    return 23;
                }
                byte v = GetR(y, out ex);
                v = Dec8(v);
                SetR(y, v, out int ex2);
                if (y == 6) return 11 + ex + ex2;
                return 4;
            }

            case 6: // LD r,n
            {
                if (y == 6 && _idx != 0)
                {
                    // LD (IX+d),n - order: d then n
                    sbyte d = (sbyte)Fetch();
                    byte n = Fetch();
                    ushort baseR = _idx == 1 ? IX : IY;
                    ushort addr = (ushort)(baseR + d);
                    Mem.Write(addr, n);
                    return 19;
                }
                byte nn = Fetch();
                SetR(y, nn, out ex);
                return (y == 6) ? 10 : 7;
            }

            case 7: // Miscellaneous: RLCA, RRCA, RLA, RRA, DAA, CPL, SCF, CCF
                switch (y)
                {
                    case 0: // RLCA
                    {
                        int c = (A >> 7) & 1;
                        A = (byte)((A << 1) | c);
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV)) | (A & (FLAG_Y | FLAG_X)) | (c != 0 ? FLAG_C : 0));
                        return 4;
                    }
                    case 1: // RRCA
                    {
                        int c = A & 1;
                        A = (byte)((A >> 1) | (c << 7));
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV)) | (A & (FLAG_Y | FLAG_X)) | (c != 0 ? FLAG_C : 0));
                        return 4;
                    }
                    case 2: // RLA
                    {
                        int c = (A >> 7) & 1;
                        A = (byte)((A << 1) | ((F & FLAG_C) != 0 ? 1 : 0));
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV)) | (A & (FLAG_Y | FLAG_X)) | (c != 0 ? FLAG_C : 0));
                        return 4;
                    }
                    case 3: // RRA
                    {
                        int c = A & 1;
                        A = (byte)((A >> 1) | ((F & FLAG_C) != 0 ? 0x80 : 0));
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV)) | (A & (FLAG_Y | FLAG_X)) | (c != 0 ? FLAG_C : 0));
                        return 4;
                    }
                    case 4: Daa(); return 4;
                    case 5: // CPL
                        A = (byte)~A;
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV | FLAG_C)) | FLAG_H | FLAG_N | (A & (FLAG_Y | FLAG_X)));
                        return 4;
                    case 6: // SCF
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV)) | FLAG_C | (A & (FLAG_Y | FLAG_X)));
                        return 4;
                    case 7: // CCF
                    {
                        bool c = (F & FLAG_C) != 0;
                        F = (byte)((F & (FLAG_S | FLAG_Z | FLAG_PV)) | (c ? FLAG_H : FLAG_C) | (A & (FLAG_Y | FLAG_X)));
                        return 4;
                    }
                }
                return 4;
        }
        return 4;
    }

    // 40-7F: LD r,r' (with HALT at 0x76)
    private int ExecuteX1(byte op, int y, int z)
    {
        if (op == 0x76) { Halted = true; return 4; }

        // When _idx != 0, (HL) encoded as register 6 uses (IX+d)/(IY+d), but H/L
        // used as OTHER operand should still refer to H/L (not IXH/IXL) per docs.
        // However, when neither operand is (HL), the DD prefix still substitutes H/L->IXH/IXL.
        // The common Z80 behavior: if one operand is (HL) [i.e., r==6], the other H/L references stay H/L.
        bool srcIsMem = z == 6;
        bool dstIsMem = y == 6;
        int savedIdx = _idx;
        if ((srcIsMem || dstIsMem) && _idx != 0)
        {
            // For (IX+d)/(IY+d), displacement is read once
            sbyte d = (sbyte)Fetch();
            ushort baseR = _idx == 1 ? IX : IY;
            ushort addr = (ushort)(baseR + d);
            // Read the non-memory register as plain H/L (not IXH/IXL)
            _idx = 0;
            byte val;
            if (srcIsMem) val = Mem.Read(addr);
            else val = GetR(z, out _);

            if (dstIsMem) Mem.Write(addr, val);
            else SetR(y, val, out _);
            _idx = savedIdx;
            return 19;
        }

        byte v = GetR(z, out int ex);
        SetR(y, v, out int ex2);
        return 4 + ex + ex2;
    }

    // 80-BF: ALU A,r
    private int ExecuteX2(byte op, int y, int z)
    {
        byte v = GetR(z, out int ex);
        int baseCyc = (z == 6) ? 7 : 4;
        switch (y)
        {
            case 0: A = Add8(A, v, 0); break;              // ADD A,r
            case 1: A = Add8(A, v, (F & FLAG_C) != 0 ? 1 : 0); break; // ADC A,r
            case 2: A = Sub8(A, v, 0); break;              // SUB r
            case 3: A = Sub8(A, v, (F & FLAG_C) != 0 ? 1 : 0); break; // SBC A,r
            case 4: A = And8(A, v); break;                  // AND r
            case 5: A = Xor8(A, v); break;                  // XOR r
            case 6: A = Or8(A, v); break;                   // OR r
            case 7: Cp8(A, v); break;                       // CP r
        }
        return baseCyc + ex;
    }

    // C0-FF
    private int ExecuteX3(byte op, int y, int z, int p, int q)
    {
        switch (z)
        {
            case 0: // RET cc
                if (Cond(y)) { PC = Pop(); return 11; }
                return 5;

            case 1:
                if (q == 0) // POP rp2
                {
                    ushort v = Pop();
                    switch (p)
                    {
                        case 0: BC = v; break;
                        case 1: DE = v; break;
                        case 2: if (_idx == 0) HL = v; else if (_idx == 1) IX = v; else IY = v; break;
                        case 3: AF = v; break;
                    }
                    return (p == 2 && _idx != 0) ? 14 : 10;
                }
                else
                {
                    switch (p)
                    {
                        case 0: PC = Pop(); return 10;                                   // RET
                        case 1:                                                          // EXX
                        {
                            ushort t;
                            t = BC; BC = BC_; BC_ = t;
                            t = DE; DE = DE_; DE_ = t;
                            t = HL; HL = HL_; HL_ = t;
                            return 4;
                        }
                        case 2: PC = _idx == 0 ? HL : (_idx == 1 ? IX : IY); return (_idx == 0) ? 4 : 8; // JP (HL/IX/IY)
                        case 3: SP = _idx == 0 ? HL : (_idx == 1 ? IX : IY); return (_idx == 0) ? 6 : 10; // LD SP,HL
                    }
                }
                return 4;

            case 2: // JP cc,nn
            {
                ushort nn = FetchWord();
                if (Cond(y)) PC = nn;
                return 10;
            }

            case 3:
                switch (y)
                {
                    case 0: PC = FetchWord(); return 10;                  // JP nn
                    case 1: return ExecuteCBPrefix();                      // CB prefix
                    case 2:                                                // OUT (n),A
                    {
                        byte n = Fetch();
                        ushort port = (ushort)((A << 8) | n);
                        Io.Out(port, A);
                        return 11;
                    }
                    case 3:                                                // IN A,(n)
                    {
                        byte n = Fetch();
                        ushort port = (ushort)((A << 8) | n);
                        A = Io.In(port);
                        return 11;
                    }
                    case 4:                                                // EX (SP),HL/IX/IY
                    {
                        ushort v = ReadWord(SP);
                        if (_idx == 0) { WriteWord(SP, HL); HL = v; }
                        else if (_idx == 1) { WriteWord(SP, IX); IX = v; }
                        else { WriteWord(SP, IY); IY = v; }
                        return (_idx == 0) ? 19 : 23;
                    }
                    case 5:                                                // EX DE,HL (always plain HL regardless of DD/FD)
                    {
                        ushort t = DE; DE = HL; HL = t;
                        return 4;
                    }
                    case 6:                                                // DI
                        IFF1 = IFF2 = false; return 4;
                    case 7:                                                // EI
                        IFF1 = IFF2 = true; return 4;
                }
                return 4;

            case 4: // CALL cc,nn
            {
                ushort nn = FetchWord();
                if (Cond(y)) { Push(PC); PC = nn; return 17; }
                return 10;
            }

            case 5:
                if (q == 0) // PUSH rp2
                {
                    ushort v = p switch
                    {
                        0 => BC,
                        1 => DE,
                        2 => _idx == 0 ? HL : (_idx == 1 ? IX : IY),
                        3 => AF,
                        _ => (ushort)0
                    };
                    Push(v);
                    return (p == 2 && _idx != 0) ? 15 : 11;
                }
                else
                {
                    switch (p)
                    {
                        case 0: // CALL nn
                        {
                            ushort nn = FetchWord();
                            Push(PC);
                            PC = nn;
                            return 17;
                        }
                        case 1: return ExecuteDDPrefix();
                        case 2: return ExecuteEDPrefix();
                        case 3: return ExecuteFDPrefix();
                    }
                }
                return 4;

            case 6: // ALU A,n
            {
                byte v = Fetch();
                switch (y)
                {
                    case 0: A = Add8(A, v, 0); break;
                    case 1: A = Add8(A, v, (F & FLAG_C) != 0 ? 1 : 0); break;
                    case 2: A = Sub8(A, v, 0); break;
                    case 3: A = Sub8(A, v, (F & FLAG_C) != 0 ? 1 : 0); break;
                    case 4: A = And8(A, v); break;
                    case 5: A = Xor8(A, v); break;
                    case 6: A = Or8(A, v); break;
                    case 7: Cp8(A, v); break;
                }
                return 7;
            }

            case 7: // RST p
                Push(PC);
                PC = (ushort)(y * 8);
                return 11;
        }
        return 4;
    }

    // DD prefix: IX variant
    private int ExecuteDDPrefix()
    {
        byte op2 = Fetch();
        IncR();
        if (op2 == 0xCB) return ExecuteDDCB(1);
        _idx = 1;
        int c = ExecuteMain(op2);
        _idx = 0;
        return c + 4; // +4 for the prefix
    }

    // FD prefix: IY variant
    private int ExecuteFDPrefix()
    {
        byte op2 = Fetch();
        IncR();
        if (op2 == 0xCB) return ExecuteDDCB(2);
        _idx = 2;
        int c = ExecuteMain(op2);
        _idx = 0;
        return c + 4;
    }
}
