namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 cassette trap. Same monitor family as MZ-700 (both run
/// 1Z-013-series), so this class mirrors <see cref="Cassette"/>'s
/// approach exactly: trap at the READ-HEADER and READ-DATA
/// <b>implementation</b> addresses ($0436 and $04D8), not at the
/// jump-table entries at $0021/$0027. The 1Z-013 monitor's L command
/// resolves through those jumps into the routines at $0436/$04D8, and
/// trapping the implementation entry point is what
/// <see cref="Cassette"/> proved reliable on MZ-700 back in v0.x.
///
/// Phase 4 fix (v1.3.0, 2026-08-28): initial Phase-1 wiring used the
/// jump-table entries ($0027/$002A) copied from
/// <see cref="Mz80aCassette"/>. That happened to work on MZ-80A
/// because SA-1510's L flow goes through those entries, but on the
/// 1Z-013B monitor the LOAD flow ran past our trap — the user saw
/// the real "PLAY" prompt instead of injection. Also mirrored MZ-700's
/// IFF1/IFF2 restore after <see cref="SynthesiseSuccess"/>: the tape
/// routines end with EI on 1Z-013A/013B, so skipping the RET without
/// re-enabling interrupts left the monitor's keyboard-scan ISR dead
/// and the machine appeared to hang.
/// </summary>
public sealed class Mz800Cassette : CassetteTrapBase
{
    public const ushort TrapReadHeader = 0x0436;
    public const ushort TrapReadData   = 0x04D8;

    // Typed field so callers wire Memory as MZ800Memory; base holds
    // it as IMemory (Mem) for its read/write path. Same shape as
    // Mz80aCassette.Memory.
    private MZ800Memory _mem = null!;
    public MZ800Memory Memory
    {
        get => _mem;
        set { _mem = value; Mem = value; }
    }

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
            WriteDataToRam(loadAddr, size);
            DataDelivered = true;
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;
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
