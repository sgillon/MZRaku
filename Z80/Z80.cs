using System;

namespace MZ700Emul.Z80;

public interface IMemory
{
    byte Read(ushort addr);
    void Write(ushort addr, byte value);
}

public interface IIoBus
{
    byte In(ushort port);
    void Out(ushort port, byte value);
}

public sealed partial class Z80Cpu
{
    // Flag bit positions
    public const byte FLAG_C = 0x01;
    public const byte FLAG_N = 0x02;
    public const byte FLAG_PV = 0x04;
    public const byte FLAG_X = 0x08;
    public const byte FLAG_H = 0x10;
    public const byte FLAG_Y = 0x20;
    public const byte FLAG_Z = 0x40;
    public const byte FLAG_S = 0x80;

    // Main register set
    public byte A, F, B, C, D, E, H, L;
    // Alternate register set
    public byte A_, F_, B_, C_, D_, E_, H_, L_;
    // Index registers
    public ushort IX, IY;
    // Stack and program counter
    public ushort SP, PC;
    // Interrupt register and refresh
    public byte I, R;
    // Interrupt flip-flops, interrupt mode, halted state
    public bool IFF1, IFF2;
    public byte IM;
    public bool Halted;

    public long TotalCycles;

    // WZ (MEMPTR) register used for undocumented flag behavior on some ops
    public ushort WZ;

    // Memory and I/O bus
    public IMemory Mem = null!;
    public IIoBus Io = null!;

    // Pending interrupt
    private bool _intRequested;

    /// <summary>
    /// Optional pre-step hook: returns true to skip fetching an opcode this cycle
    /// (because the hook redirected PC or handled the instruction in C#).
    /// </summary>
    public Func<bool>? PreStep;

    // Parity table
    private static readonly byte[] ParityTable = new byte[256];

    static Z80Cpu()
    {
        for (int i = 0; i < 256; i++)
        {
            int p = 0;
            int v = i;
            for (int b = 0; b < 8; b++) { p ^= v & 1; v >>= 1; }
            ParityTable[i] = (p == 0) ? FLAG_PV : (byte)0;
        }
    }

    public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }
    public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)value; } }
    public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)value; } }
    public ushort AF { get => (ushort)((A << 8) | F); set { A = (byte)(value >> 8); F = (byte)value; } }
    public ushort AF_ { get => (ushort)((A_ << 8) | F_); set { A_ = (byte)(value >> 8); F_ = (byte)value; } }
    public ushort BC_ { get => (ushort)((B_ << 8) | C_); set { B_ = (byte)(value >> 8); C_ = (byte)value; } }
    public ushort DE_ { get => (ushort)((D_ << 8) | E_); set { D_ = (byte)(value >> 8); E_ = (byte)value; } }
    public ushort HL_ { get => (ushort)((H_ << 8) | L_); set { H_ = (byte)(value >> 8); L_ = (byte)value; } }

    public byte IXH { get => (byte)(IX >> 8); set => IX = (ushort)((IX & 0x00FF) | (value << 8)); }
    public byte IXL { get => (byte)IX; set => IX = (ushort)((IX & 0xFF00) | value); }
    public byte IYH { get => (byte)(IY >> 8); set => IY = (ushort)((IY & 0x00FF) | (value << 8)); }
    public byte IYL { get => (byte)IY; set => IY = (ushort)((IY & 0xFF00) | value); }

    public void Reset()
    {
        A = F = B = C = D = E = H = L = 0xFF;
        A_ = F_ = B_ = C_ = D_ = E_ = H_ = L_ = 0;
        IX = IY = 0xFFFF;
        SP = 0xFFFF;
        PC = 0;
        I = R = 0;
        IFF1 = IFF2 = false;
        IM = 0;
        Halted = false;
        WZ = 0;
        _intRequested = false;
        TotalCycles = 0;
    }

    public void RequestInterrupt() => _intRequested = true;
    public void ClearInterrupt() => _intRequested = false;

    private void IncR() { R = (byte)((R & 0x80) | ((R + 1) & 0x7F)); }

    private byte Fetch()
    {
        byte v = Mem.Read(PC); PC++; return v;
    }

    private ushort FetchWord()
    {
        byte lo = Fetch();
        byte hi = Fetch();
        return (ushort)(lo | (hi << 8));
    }

    private byte ReadByte(ushort a) => Mem.Read(a);
    private void WriteByte(ushort a, byte v) => Mem.Write(a, v);

    private ushort ReadWord(ushort a)
    {
        byte lo = Mem.Read(a);
        byte hi = Mem.Read((ushort)(a + 1));
        return (ushort)(lo | (hi << 8));
    }

    private void WriteWord(ushort a, ushort v)
    {
        Mem.Write(a, (byte)v);
        Mem.Write((ushort)(a + 1), (byte)(v >> 8));
    }

    private void Push(ushort v)
    {
        SP--; Mem.Write(SP, (byte)(v >> 8));
        SP--; Mem.Write(SP, (byte)v);
    }

    private ushort Pop()
    {
        byte lo = Mem.Read(SP); SP++;
        byte hi = Mem.Read(SP); SP++;
        return (ushort)(lo | (hi << 8));
    }

    // Flag helpers -----------------------------------------------------------

    private void SetFlag(byte mask, bool cond)
    {
        if (cond) F |= mask; else F &= (byte)~mask;
    }

    private bool GetFlag(byte mask) => (F & mask) != 0;

    // Set S, Z, Y, X flags from an 8-bit result
    private void SetSZYX(byte r)
    {
        F = (byte)((F & ~(FLAG_S | FLAG_Z | FLAG_Y | FLAG_X)) |
                   (r & (FLAG_S | FLAG_Y | FLAG_X)) |
                   (r == 0 ? FLAG_Z : 0));
    }

    // Arithmetic -------------------------------------------------------------

    private byte Add8(byte a, byte b, int carry)
    {
        int res = a + b + carry;
        byte r = (byte)res;
        F = (byte)(
            (r & (FLAG_S | FLAG_Y | FLAG_X)) |
            (r == 0 ? FLAG_Z : 0) |
            (((a ^ b ^ r) & 0x10) != 0 ? FLAG_H : 0) |
            ((((a ^ ~b) & (a ^ r) & 0x80) != 0) ? FLAG_PV : 0) |
            (res > 0xFF ? FLAG_C : 0)
        );
        return r;
    }

    private byte Sub8(byte a, byte b, int carry)
    {
        int res = a - b - carry;
        byte r = (byte)res;
        F = (byte)(
            (r & (FLAG_S | FLAG_Y | FLAG_X)) |
            (r == 0 ? FLAG_Z : 0) |
            FLAG_N |
            (((a ^ b ^ r) & 0x10) != 0 ? FLAG_H : 0) |
            ((((a ^ b) & (a ^ r) & 0x80) != 0) ? FLAG_PV : 0) |
            ((res & 0x100) != 0 ? FLAG_C : 0)
        );
        return r;
    }

    private void Cp8(byte a, byte b)
    {
        int res = a - b;
        byte r = (byte)res;
        F = (byte)(
            (r & FLAG_S) |
            (b & (FLAG_Y | FLAG_X)) |
            (r == 0 ? FLAG_Z : 0) |
            FLAG_N |
            (((a ^ b ^ r) & 0x10) != 0 ? FLAG_H : 0) |
            ((((a ^ b) & (a ^ r) & 0x80) != 0) ? FLAG_PV : 0) |
            ((res & 0x100) != 0 ? FLAG_C : 0)
        );
    }

    private byte And8(byte a, byte b)
    {
        byte r = (byte)(a & b);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | FLAG_H | ParityTable[r]);
        return r;
    }

    private byte Or8(byte a, byte b)
    {
        byte r = (byte)(a | b);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r]);
        return r;
    }

    private byte Xor8(byte a, byte b)
    {
        byte r = (byte)(a ^ b);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r]);
        return r;
    }

    private byte Inc8(byte v)
    {
        byte r = (byte)(v + 1);
        F = (byte)(
            (F & FLAG_C) |
            (r & (FLAG_S | FLAG_Y | FLAG_X)) |
            (r == 0 ? FLAG_Z : 0) |
            ((v & 0x0F) == 0x0F ? FLAG_H : 0) |
            (v == 0x7F ? FLAG_PV : 0)
        );
        return r;
    }

    private byte Dec8(byte v)
    {
        byte r = (byte)(v - 1);
        F = (byte)(
            (F & FLAG_C) |
            (r & (FLAG_S | FLAG_Y | FLAG_X)) |
            (r == 0 ? FLAG_Z : 0) |
            FLAG_N |
            ((v & 0x0F) == 0x00 ? FLAG_H : 0) |
            (v == 0x80 ? FLAG_PV : 0)
        );
        return r;
    }

    private ushort Add16(ushort a, ushort b)
    {
        int res = a + b;
        ushort r = (ushort)res;
        F = (byte)(
            (F & (FLAG_S | FLAG_Z | FLAG_PV)) |
            (((a ^ b ^ r) >> 8) & FLAG_H) |
            ((r >> 8) & (FLAG_Y | FLAG_X)) |
            (res > 0xFFFF ? FLAG_C : 0)
        );
        return r;
    }

    private ushort Adc16(ushort a, ushort b)
    {
        int carry = (F & FLAG_C) != 0 ? 1 : 0;
        int res = a + b + carry;
        ushort r = (ushort)res;
        F = (byte)(
            ((r & 0x8000) != 0 ? FLAG_S : 0) |
            (r == 0 ? FLAG_Z : 0) |
            (((a ^ b ^ r) >> 8) & FLAG_H) |
            ((r >> 8) & (FLAG_Y | FLAG_X)) |
            ((((a ^ ~b) & (a ^ r) & 0x8000) != 0) ? FLAG_PV : 0) |
            (res > 0xFFFF ? FLAG_C : 0)
        );
        return r;
    }

    private ushort Sbc16(ushort a, ushort b)
    {
        int carry = (F & FLAG_C) != 0 ? 1 : 0;
        int res = a - b - carry;
        ushort r = (ushort)res;
        F = (byte)(
            ((r & 0x8000) != 0 ? FLAG_S : 0) |
            (r == 0 ? FLAG_Z : 0) |
            FLAG_N |
            (((a ^ b ^ r) >> 8) & FLAG_H) |
            ((r >> 8) & (FLAG_Y | FLAG_X)) |
            ((((a ^ b) & (a ^ r) & 0x8000) != 0) ? FLAG_PV : 0) |
            ((res & 0x10000) != 0 ? FLAG_C : 0)
        );
        return r;
    }

    private void Daa()
    {
        int a = A;
        int correction = 0;
        bool setC = (F & FLAG_C) != 0;
        bool setH = false;
        if ((F & FLAG_N) != 0)
        {
            if ((F & FLAG_H) != 0 || (a & 0x0F) > 9) { correction |= 0x06; if ((a & 0x0F) < 6) setH = true; }
            if (setC || a > 0x99) { correction |= 0x60; setC = true; }
            a = (a - correction) & 0xFF;
        }
        else
        {
            if ((F & FLAG_H) != 0 || (a & 0x0F) > 9) { correction |= 0x06; }
            if (setC || a > 0x99) { correction |= 0x60; setC = true; }
            int before = a;
            a = (a + correction) & 0xFF;
            setH = ((before ^ a) & 0x10) != 0;
        }
        A = (byte)a;
        F = (byte)(
            (F & FLAG_N) |
            (A & (FLAG_S | FLAG_Y | FLAG_X)) |
            (A == 0 ? FLAG_Z : 0) |
            (setH ? FLAG_H : 0) |
            ParityTable[A] |
            (setC ? FLAG_C : 0)
        );
    }

    // Rotates / shifts -------------------------------------------------------

    private byte Rlc(byte v)
    {
        int c = (v >> 7) & 1;
        byte r = (byte)((v << 1) | c);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Rrc(byte v)
    {
        int c = v & 1;
        byte r = (byte)((v >> 1) | (c << 7));
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Rl(byte v)
    {
        int c = (v >> 7) & 1;
        byte r = (byte)((v << 1) | ((F & FLAG_C) != 0 ? 1 : 0));
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Rr(byte v)
    {
        int c = v & 1;
        byte r = (byte)((v >> 1) | ((F & FLAG_C) != 0 ? 0x80 : 0));
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Sla(byte v)
    {
        int c = (v >> 7) & 1;
        byte r = (byte)(v << 1);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Sra(byte v)
    {
        int c = v & 1;
        byte r = (byte)((v >> 1) | (v & 0x80));
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Sll(byte v)
    {
        int c = (v >> 7) & 1;
        byte r = (byte)((v << 1) | 1);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private byte Srl(byte v)
    {
        int c = v & 1;
        byte r = (byte)(v >> 1);
        F = (byte)((r & (FLAG_S | FLAG_Y | FLAG_X)) | (r == 0 ? FLAG_Z : 0) | ParityTable[r] | (c != 0 ? FLAG_C : 0));
        return r;
    }

    private void Bit(int bit, byte v, byte flagSrc)
    {
        byte mask = (byte)(1 << bit);
        bool isSet = (v & mask) != 0;
        F = (byte)(
            (F & FLAG_C) |
            FLAG_H |
            (isSet ? 0 : FLAG_Z | FLAG_PV) |
            (bit == 7 && isSet ? FLAG_S : 0) |
            (flagSrc & (FLAG_Y | FLAG_X))
        );
    }

    // Interrupt handling -----------------------------------------------------

    public int HandleInterruptIfPending()
    {
        if (!_intRequested || !IFF1) return 0;
        _intRequested = false;
        if (Halted) { Halted = false; PC++; }
        IFF1 = IFF2 = false;
        IncR();
        switch (IM)
        {
            case 0:
            case 1:
                Push(PC);
                PC = 0x0038;
                return 13;
            case 2:
                Push(PC);
                ushort v = (ushort)((I << 8) | 0xFF);
                PC = ReadWord(v);
                return 19;
        }
        return 13;
    }

    // Ring buffer of last N PC values for post-mortem tracing
    public readonly ushort[] PcTrace = new ushort[256];
    public int PcTraceIdx;
    public bool PcTraceEnabled;
    public bool PcTraceFrozen;

    // Optional PC histogram, indexed by PC>>4 (16-byte buckets). When
    // non-null, every executed instruction bumps its bucket — used to
    // see hot PC ranges during BASIC MUSIC playback.
    public int[]? PcHistogram;

    public int Step()
    {
        int intCycles = HandleInterruptIfPending();
        if (intCycles > 0) { TotalCycles += intCycles; return intCycles; }

        if (PreStep != null && PreStep())
        {
            // Hook handled the "instruction" - count a minimal cycle cost
            TotalCycles += 4;
            return 4;
        }

        if (Halted)
        {
            IncR();
            TotalCycles += 4;
            return 4;
        }

        if (PcTraceEnabled && !PcTraceFrozen)
        {
            PcTrace[PcTraceIdx] = PC;
            PcTraceIdx = (PcTraceIdx + 1) & 0xFF;
        }
        if (PcHistogram != null) PcHistogram[PC >> 4]++;

        byte op = Fetch();
        IncR();
        int cycles = ExecuteMain(op);
        TotalCycles += cycles;
        return cycles;
    }
}
