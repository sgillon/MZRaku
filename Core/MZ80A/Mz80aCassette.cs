namespace MZRaku.Core;

/// <summary>
/// Sharp MZ-80A cassette-tape trap harness. SA-1510 exposes tape
/// I/O via well-defined jump-table entries per Owner's Manual
/// §2.1.2:
///
///   $0021 WRINF — write header to tape
///   $0024 WRDAT — write data body
///   $0027 RDINF — read header from tape
///   $002A RDDAT — read data body
///   $002D VERFY — verify tape against memory
///
/// Return convention (documented for all five): C flag = 0 on
/// success, C flag = 1 with A = 1 (checksum) or A = 2 (BREAK).
///
/// The header buffer at $10F0-$1163 uses the same layout as MZ-700:
///
///   $10F0        type (01=MC, 02=BASIC text, 03=BASIC data, 04=ASCII,
///                05=Reloc, A0/A1=PASCAL)
///   $10F1-$1101  file name (17 bytes, 0D-terminated, 16 chars max)
///   $1102-$1103  file size (little-endian)
///   $1104-$1105  load address (little-endian)
///   $1106-$1107  execution address (little-endian)
///   $1108-$1163  comment
///
/// The <see cref="MzfImage"/> record is Sharp-family shared (v1.2
/// audit F-001 hoisted it out of Cassette for exactly this reason).
///
/// The LOAD-header + LOAD-data trap pattern is shared with MZ-700
/// via <see cref="CassetteTrapBase"/> (v1.2 audit F-025). This
/// class provides the SA-1510-specific trap addresses. SAVE goes
/// through the shared writer too (v1.5 4.7, <see cref="WriteTrap"/>).
/// </summary>
internal sealed class Mz80aCassette : CassetteTrapBase
{
    public const ushort TrapRdInf = 0x0027;
    public const ushort TrapRdDat = 0x002A;

    // SA-1510's tape write (v1.5 4.7, found by disassembly). WRINF
    // ($0021 → $0436: header, E=$CC) and WRDAT ($0024 → $0470: data,
    // E=$53, address and size from $1104 / $1102) set D=$D7 and share
    // the code from $0444:
    //   $0444 CALL $071A   checksum
    //   $0447 CALL $06A3   wait for RECORD.PLAY, motor on
    //   $044A JR C,$0464
    //   $044C..$045D       header only: print WRITING "name"
    //   $045E CALL $077A   leader
    //   $0461 CALL $0485   the bytes
    //   $0464 JP $0552     pop, motor off ($0700 keeps AF), RET
    private const ushort WriteSenseWait = 0x0447;
    private static readonly byte[] WriteSenseWaitBytes = { 0xCD, 0xA3, 0x06, 0x38, 0x18 };
    private const ushort WriteSenseDone = 0x044A;
    private const ushort WriteBody = 0x045E;
    private static readonly byte[] WriteBodyBytes = { 0xCD, 0x7A, 0x07, 0xCD, 0x85, 0x04 };
    private const ushort WriteExit = 0x0464;
    private const byte WriteDevice = 0xD7;
    private const byte WriteHeaderMark = 0xCC;
    private const ushort HeaderSizeAddr = 0x1102;
    private const ushort HeaderLoadAddr = 0x1104;

    // Typed field so callers wire Memory as Mz80aMemory; base holds
    // it as IMemory (Mem) for its read/write path.
    private Mz80aMemory _mem = null!;
    public Mz80aMemory Memory
    {
        get => _mem;
        set { _mem = value; Mem = value; }
    }

    /// <summary>
    /// Traps SA-1510's tape read entry points when a Pending image
    /// is queued. Injects header / data into RAM, synthesises a
    /// successful RET (CY=0), then advances state so a follow-up
    /// call picks up the next stage.
    /// </summary>
    public override bool OnPreStep()
    {
        ushort pc = Cpu.PC;
        if ((pc == WriteSenseWait || pc == WriteBody) && WriteTrap(pc)) return true;
        if (Pending == null) return false;
        if (pc == TrapRdInf && !HeaderDelivered)
        {
            HeaderTrapHits++;
            WriteHeaderToBuffer();
            HeaderDelivered = true;
            SynthesiseSuccess();
            return true;
        }
        if (pc == TrapRdDat && HeaderDelivered && !DataDelivered)
        {
            DataTrapHits++;
            WriteDataToRam(Pending.LoadAddr, Pending.Data.Length);
            DataDelivered = true;
            SynthesiseSuccess();
            // One-shot — clear pending so a second L command won't
            // re-inject the same image.
            Pending = null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Takes a SAVE from SA-1510's tape write. At the RECORD.PLAY wait
    /// it carries on as if the keys were down, so the ROM still prints
    /// WRITING "name"; at the leader it takes the block, hands it to
    /// <see cref="CassetteTrapBase.TakeSaveBlock"/> and leaves through
    /// the ROM's own exit with CY=0. Only while SA-1510 is at $0000
    /// (not swapped out by $E00C).
    /// </summary>
    private bool WriteTrap(ushort pc)
    {
        if (_mem.RomSwapped || Cpu.D != WriteDevice) return false;
        if (pc == WriteSenseWait)
        {
            if (!RomBytesAt(WriteSenseWait, WriteSenseWaitBytes)) return false;
            Cpu.F &= 0xFE;
            Cpu.PC = WriteSenseDone;
            return true;
        }
        if (!RomBytesAt(WriteBody, WriteBodyBytes)) return false;
        bool header = Cpu.E == WriteHeaderMark;
        ushort src = header ? HeaderBufferAddr : ReadWord(HeaderLoadAddr);
        int size = header ? HeaderSize : ReadWord(HeaderSizeAddr);
        var block = new byte[size];
        for (int i = 0; i < size; i++)
            block[i] = _mem.Read((ushort)(src + i));
        TakeSaveBlock(header, block);
        Cpu.A = 0;
        Cpu.F &= 0xFE;
        Cpu.PC = WriteExit;
        return true;
    }

    private bool RomBytesAt(ushort addr, byte[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
            if (_mem.Rom[addr + i] != expected[i]) return false;
        return true;
    }

    private ushort ReadWord(ushort addr) =>
        (ushort)(_mem.Read(addr) | _mem.Read((ushort)(addr + 1)) << 8);

    // SA-5510 BASIC program state (v1.5 Phase 2.4, found by diffing RAM
    // after a typed LOAD against a direct inject, then reading LOAD's
    // own code at $2F19).
    private const ushort SBasicTextStart = 0x505C;      // LOAD always loads here
    private const ushort SBasicVarTable = 0x4E4E;       // 36 pointers, up to $4E96
    private const int SBasicVarTableEntries = 36;
    private const int SBasicHeaderKept = 0x1164 - HeaderBufferAddr;   // stop short of the key-scan state
    // SA-5510's relink routine at $1AC7 (LD HL,$505C; LD E,(HL); INC HL;
    // LD D,(HL); DEC HL), to tell SA-5510 from any other BASIC in RAM.
    private const ushort SBasicRelink = 0x1AC7;
    private static readonly byte[] SBasicRelinkEntry = { 0x21, 0x5C, 0x50, 0x5E, 0x23, 0x56, 0x2B };

    /// <summary>
    /// Puts an SA-5510 BASIC program (type $02) into RAM the way BASIC's
    /// own LOAD leaves it, so RUN can follow without LOAD being typed.
    /// Call at SA-5510's Ready prompt on a freshly started BASIC.
    /// Returns false, changing nothing, if SA-5510 isn't the BASIC in
    /// RAM; the caller then types LOAD instead.
    ///
    /// What LOAD does after reading the data, copied here:
    /// <list type="bullet">
    ///   <item>Loads at BASIC's text start ($505C), whatever load
    ///   address the header gives.</item>
    ///   <item>Relinks the lines ($1AC7): a saved program stores each
    ///   line's first two bytes as the line's length, and LOAD turns
    ///   each into the address of the next line. GOSUB and GOTO walk
    ///   these links, so without the relink `GOSUB` stops with Error
    ///   16 (why the 2026-07 attempt, which only set the variable
    ///   table, failed).</item>
    ///   <item>Moves the variable table ($4E4E, 36 pointers) up past
    ///   the program ($1888): entry i = program end + 2i.</item>
    /// </list>
    /// A RAM diff against a typed LOAD then differs only in traces of
    /// the typed command itself (key and line buffers, the tokenised
    /// LOAD, the tape flag at $2D74, the stack).
    /// </summary>
    internal bool InjectBasicProgram(MzfImage img)
    {
        var ram = _mem.Ram;
        for (int i = 0; i < SBasicRelinkEntry.Length; i++)
            if (ram[SBasicRelink + i] != SBasicRelinkEntry[i]) return false;

        // The header, but not its last 12 bytes: SA-1510's key scan keeps
        // its state in $1164–$116E ($08B5 fills 9 bytes from $1165;
        // $116E is a flag), inside the header buffer. A file's header
        // holds whatever was there when it was saved — "no keys" ($FF)
        // in cricket.mzf, $00 in COLDITZ — and writing $00 there makes
        // the next scan miss the first key typed, so RUN arrived as
        // "UN" (the R-drop noted 2026-07).
        for (int i = 0; i < SBasicHeaderKept; i++)
            ram[HeaderBufferAddr + i] = img.Header[i];
        Array.Copy(img.Data, 0, ram, SBasicTextStart, img.Data.Length);

        // Relink, stopping at the $0000 end marker (or the data's end).
        int end = SBasicTextStart + img.Data.Length;
        int line = SBasicTextStart;
        while (line + 1 < end)
        {
            int length = ram[line] | ram[line + 1] << 8;
            if (length == 0) break;
            int next = line + length;
            ram[line] = (byte)next;
            ram[line + 1] = (byte)(next >> 8);
            line = next;
        }

        for (int i = 0; i < SBasicVarTableEntries; i++)
        {
            int p = end + 2 * i;
            ram[SBasicVarTable + 2 * i] = (byte)p;
            ram[SBasicVarTable + 2 * i + 1] = (byte)(p >> 8);
        }
        RaiseLoaded($"Injected BASIC: {img.Filename} at ${SBasicTextStart:X4} size={img.Data.Length}");
        return true;
    }
}
