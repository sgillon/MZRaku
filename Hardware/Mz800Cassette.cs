namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 cassette trap. The MZ-800 boots into 1Z-016B (its own
/// monitor at CPU $E000-$FFFF) which handles the L command itself
/// rather than delegating to 1Z-013B at $0000-$0FFF. 1Z-016B's L
/// subroutine at $EB54 calls the 1Z-013B tape primitives:
///   $EB54: CALL $04D8   — read header (128 bytes into $10F0)
///   $EB6D: JP  $04F8    — read data  (size + load addr from header)
/// then (on success) JPs to $E99D which auto-runs the program from
/// the header's exec address.
///
/// So on MZ-800 the trap points are $04D8 (header) and $04F8 (data)
/// — the opposite of what MZ-700's L flow uses. MZ-700's L handler in
/// 1Z-013A calls $04D8 which internally does header + CALL $050E for
/// data, so MZ-700 gets away with a single $04D8 trap; on MZ-800
/// 1Z-016B does the two reads separately and $04F8 must be trapped
/// too or the second read hits real hardware and spins forever.
///
/// $0436 is NOT the LOAD header entry — its inline text at $0467
/// reads "WRITING &lt;CR&gt;". It's the SAVE header routine. Old
/// Phase 4 wiring trapped it as READ HEADER (copied from an incorrect
/// MZ-700 comment); the trap was dead on MZ-800 because 1Z-016B's L
/// never called it.
///
/// Trap addresses fixed 2026-08-30 (Phase 5 diagnostic session):
/// pinned down by walking the L flow from PC=$0E77 (mid-scroll) back
/// through ret=$EB57 in the trap log message, then disassembling
/// 1Z-016B's L subroutine and the 1Z-013B tape primitives it calls.
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
    public MZ800Memory.BankConfig LastConfig;

    public override bool OnPreStep()
    {
        if (Pending == null) return false;
        ushort pc = Cpu.PC;
        if (pc == TrapReadHeader && !HeaderDelivered)
        {
            HeaderTrapHits++;
            WriteHeaderToBuffer();
            HeaderDelivered = true;
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;
            LastHeaderRetPc = Cpu.PC;
            return true;
        }
        if (pc == TrapReadData && !DataDelivered)
        {
            DataTrapHits++;
            // $04D8 reads data using the header at $10F0. Match
            // MZ-700's approach: pull load address + size from the
            // in-RAM header (not from Pending.LoadAddr) so any
            // header edits the monitor made before the CALL are
            // honoured. Also ensures Header gets written first if
            // the caller somehow reached the data-read entry
            // without the header trap firing.
            if (!HeaderDelivered)
            {
                WriteHeaderToBuffer();
                HeaderDelivered = true;
            }
            ushort loadAddr = (ushort)(Mem.Read(HeaderBufferAddr + 0x14) | (Mem.Read(HeaderBufferAddr + 0x15) << 8));
            ushort size = (ushort)(Mem.Read(HeaderBufferAddr + 0x12) | (Mem.Read(HeaderBufferAddr + 0x13) << 8));
            ushort execAddr = (ushort)(Mem.Read(HeaderBufferAddr + 0x16) | (Mem.Read(HeaderBufferAddr + 0x17) << 8));
            WriteDataToRam(loadAddr, size);
            DataDelivered = true;
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;

            LastDataRetPc = Cpu.PC;
            LastExecAddr = execAddr;
            LastLoadAddr = loadAddr;
            LastSize = size;
            LastWasMz700Mode = _mem.Mz700Mode;
            LastConfig = _mem.Config;

            RaiseLoaded(
                $"TRAP LOAD: {Pending!.Filename} exec=${execAddr:X4} load=${loadAddr:X4} size={size} " +
                $"| ret=${LastDataRetPc:X4} mode={(_mem.Mz700Mode ? "MZ700" : "MZ800")} cfg={_mem.Config}");

            // Clear delivered flags after Pending goes so a follow-up
            // Queue starts fresh. Same shape as MZ-700's Cassette.cs.
            Pending = null;
            HeaderDelivered = false;
            DataDelivered = false;
            return true;
        }
        return false;
    }
}
