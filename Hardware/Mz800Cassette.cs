using System;
using System.Collections.Generic;

namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 cassette: a mounted tape (a sequence of files) served to the
/// two sets of tape routines MZ-800 software reads through.
///
/// <para><b>1Z-013B monitor primitives</b> — the IPL's C option and
/// the 1Z-016B L command call these with the monitor ROM at $0000:
///   $EB54: CALL $04D8   — read header (128 bytes into $10F0)
///   $EB6D: JP  $04F8    — read data  (size + load addr from header)
/// then (on success) JP to $E99D, which runs the program from the
/// header's exec address. MZ-700's L handler reads data from inside
/// $04D8, so it needs one trap; 1Z-016B does the two reads separately,
/// so $04F8 is trapped too or the second read spins on real hardware.
/// $0436 is the SAVE header routine (inline text "WRITING" at $0467),
/// not a LOAD entry.</para>
///
/// <para><b>1Z-016 BASIC's own tape code</b> — BASIC bit-bangs the tape
/// itself with the ROM banked out; see <see cref="BasicTrap"/>.</para>
///
/// Trap addresses for the monitor path pinned 2026-08-30 by walking the
/// L flow back from ret=$EB57 and disassembling 1Z-016B's L subroutine.
/// </summary>
public sealed class Mz800Cassette : CassetteTrapBase
{
    public const ushort TrapReadHeader = 0x04D8;
    public const ushort TrapReadData   = 0x04F8;

    // Typed field so callers wire Memory as MZ800Memory; base holds
    // it as IMemory (Mem) for its read/write path. Same shape as
    // Mz80aCassette.Memory.
    private MZ800Memory _mem = null!;
    public MZ800Memory Memory
    {
        get => _mem;
        set { _mem = value; Mem = value; }
    }

    // Phase 5 blank-screen diagnostics: capture the trap-time state so
    // MainForm can show it in the OnLoaded status message. Zeroed until
    // the first trap fires.
    public ushort LastHeaderRetPc;
    public ushort LastDataRetPc;
    public ushort LastExecAddr;
    public ushort LastLoadAddr;
    public ushort LastSize;
    public bool LastWasMz700Mode;
    public string LastBankState = "";

    // ---- The tape -------------------------------------------------------
    //
    // A whole cassette stays mounted: a sequence of files and a head
    // position. Pending (the base's "image under the head") is always
    // _tape[_pos], or null once the tape has run out. A header read on a
    // file whose data was never read moves past it first — that is how a
    // named LOAD skips non-matching files on a real tape.
    private readonly List<MzfImage> _tape = new();
    private int _pos;

    /// <summary>Insert a cassette holding <paramref name="files"/>,
    /// rewound to the first.</summary>
    public void Mount(IReadOnlyList<MzfImage> files)
    {
        _tape.Clear();
        _tape.AddRange(files);
        _pos = 0;
        HeaderDelivered = false;
        DataDelivered = false;
        _basicReadWaiting = false;
        SyncPending();
    }

    public override void Queue(MzfImage image) => Mount(new[] { image });

    public override void ResetTrapState()
    {
        base.ResetTrapState();
        _tape.Clear();
        _pos = 0;
        _basicReadWaiting = false;
    }

    private void SyncPending() => Pending = _pos < _tape.Count ? _tape[_pos] : null;

    /// <summary>Position on the next header. Returns false at end of tape.</summary>
    private bool BeginHeader()
    {
        if (HeaderDelivered) { _pos++; HeaderDelivered = false; SyncPending(); }
        return Pending != null;
    }

    private void FinishFile()
    {
        _pos++;
        HeaderDelivered = false;
        DataDelivered = false;
        SyncPending();
    }

    // ---- 1Z-016 BASIC's own tape code -------------------------------------
    //
    // MZ-800 BASIC reads the tape itself ($38C5-$3B8F, PPI port C) rather
    // than calling the 1Z-013B primitives. Header and data reads share
    // one entry:
    //   $386C  LD A,$CC / JR $3872   read header (HL=$10F0, BC=128)
    //   $3870  LD A,$53              read data   (HL=dest, BC=size)
    //   $3872  LD ($3865),SP / LD SP,$10F0 / PUSH DE / LD D,$D2 / LD E,A
    //          ... motor + PLAY wait ($38C5), read ...
    //   $385F  DI / CALL $3B68 (motor off) / POP DE / LD SP,<saved>
    //          / PUSH AF / RST $18,$11 / POP AF / RET    — common exit
    // Success is A=0, CY=0.
    private const ushort BasicReadEntry = 0x3872;
    private const ushort BasicReadExit = 0x385F;
    private const ushort BasicReadStackTop = 0x10EE;   // $10F0 after PUSH DE
    private const ushort BasicTapeCodeStart = 0x3881;  // past the PUSH DE
    private const ushort BasicTapeCodeEnd = 0x3BA0;
    private const ushort BasicSignatureAddr = 0x386C;
    private static readonly byte[] BasicReadSignature =
        { 0x3E, 0xCC, 0x18, 0x02, 0x3E, 0x53, 0xED, 0x73, 0x65, 0x38, 0x31, 0xF0, 0x10 };

    // A read BASIC started with nothing under the head; served if a tape
    // is inserted while it waits for PLAY.
    private bool _basicReadWaiting;
    private bool _basicWaitHeader;
    private ushort _basicWaitDest;
    private ushort _basicWaitSize;

    private bool IsBasicReadEntry()
    {
        for (int i = 0; i < BasicReadSignature.Length; i++)
            if (_mem.Read((ushort)(BasicSignatureAddr + i)) != BasicReadSignature[i]) return false;
        return true;
    }

    /// <summary>Serve one 1Z-016 read. Returns false at end of tape.</summary>
    private bool ServeBasicRead(bool header, ushort dest, ushort size)
    {
        if (header)
        {
            if (!BeginHeader()) return false;
            HeaderTrapHits++;
            int n = Math.Min((int)size, HeaderSize);
            for (int i = 0; i < n; i++) _mem.Write((ushort)(dest + i), Pending!.Header[i]);
            HeaderDelivered = true;
            return true;
        }
        if (Pending == null) return false;
        DataTrapHits++;
        int len = Math.Min((int)size, Pending.Data.Length);
        for (int i = 0; i < len; i++) _mem.Write((ushort)(dest + i), Pending.Data[i]);
        RaiseLoaded($"TAPE LOAD: {Pending.Filename} -> ${dest:X4} size={len}");
        FinishFile();
        return true;
    }

    /// <summary>
    /// 1Z-016 tape reads. With a file under the head, the read is served
    /// at its entry ($3872) and returns straight to the caller. With none,
    /// the real code runs on into its PLAY prompt and wait; a tape mounted
    /// then is served from inside the routine by jumping to the common
    /// exit, which restores the caller's stack and BASIC's state.
    /// </summary>
    private bool BasicTrap(ushort pc)
    {
        if (pc == BasicReadEntry && IsBasicReadEntry())
        {
            bool header = Cpu.A == 0xCC;
            if (ServeBasicRead(header, Cpu.HL, Cpu.BC))
            {
                _basicReadWaiting = false;
                Cpu.A = 0;
                SynthesiseSuccess();
                return true;
            }
            _basicReadWaiting = true;
            _basicWaitHeader = header;
            _basicWaitDest = Cpu.HL;
            _basicWaitSize = Cpu.BC;
            return false;
        }
        if (_basicReadWaiting && Pending != null
            && pc >= BasicTapeCodeStart && pc < BasicTapeCodeEnd)
        {
            _basicReadWaiting = false;
            if (!ServeBasicRead(_basicWaitHeader, _basicWaitDest, _basicWaitSize)) return false;
            Cpu.A = 0;
            Cpu.F &= 0xFE;
            Cpu.SP = BasicReadStackTop;
            Cpu.PC = BasicReadExit;
            return true;
        }
        return false;
    }

    public override bool OnPreStep()
    {
        ushort pc = Cpu.PC;
        // $04D8 / $04F8 are the 1Z-013B routines only while that ROM is
        // mapped; with DRAM at $0000 (BASIC) they are someone else's code.
        if (!_mem.RomLow) return BasicTrap(pc);
        if (Pending == null) return false;
        if (pc == TrapReadHeader)
        {
            if (!BeginHeader()) return false;
            HeaderTrapHits++;
            WriteHeaderToBuffer();
            HeaderDelivered = true;
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;
            LastHeaderRetPc = Cpu.PC;
            return true;
        }
        if (pc == TrapReadData)
        {
            DataTrapHits++;
            // Read data using the header at $10F0 (as MZ-700 does), so
            // header edits the monitor made before the CALL are honoured.
            // Writes the header first if the caller reached the data
            // entry without the header trap firing.
            if (!HeaderDelivered)
            {
                WriteHeaderToBuffer();
                HeaderDelivered = true;
            }
            ushort loadAddr = (ushort)(Mem.Read(HeaderBufferAddr + 0x14) | (Mem.Read(HeaderBufferAddr + 0x15) << 8));
            ushort size = (ushort)(Mem.Read(HeaderBufferAddr + 0x12) | (Mem.Read(HeaderBufferAddr + 0x13) << 8));
            ushort execAddr = (ushort)(Mem.Read(HeaderBufferAddr + 0x16) | (Mem.Read(HeaderBufferAddr + 0x17) << 8));
            WriteDataToRam(loadAddr, size);
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;

            LastDataRetPc = Cpu.PC;
            LastExecAddr = execAddr;
            LastLoadAddr = loadAddr;
            LastSize = size;
            LastWasMz700Mode = _mem.Mz700Mode;
            LastBankState = _mem.BankState;

            RaiseLoaded(
                $"TRAP LOAD: {Pending!.Filename} exec=${execAddr:X4} load=${loadAddr:X4} size={size} " +
                $"| ret=${LastDataRetPc:X4} mode={(_mem.Mz700Mode ? "MZ700" : "MZ800")} bank={_mem.BankState}");

            FinishFile();
            return true;
        }
        return false;
    }
}
