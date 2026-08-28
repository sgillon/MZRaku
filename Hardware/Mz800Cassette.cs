namespace MZRaku.Hardware;

/// <summary>
/// MZ-800 cassette trap — thin subclass of <see cref="CassetteTrapBase"/>.
/// Per the pre-Phase-1 research (see
/// <c>_mz800info/MZ800-RESEARCH-2026-08-29.md</c>), the 1Z-013B monitor
/// keeps the same tape-trap jump-table entries as MZ-700's SA-1510/
/// 1Z-013A: RDINF at $0027 and RDDAT at $002A. So the trap shape
/// ports directly — same addresses, same header-buffer layout at
/// $10F0, same MZF header format.
///
/// Phase 1 wires the trap detection but nothing queues an image here
/// yet — Phase 4 lands the full LOAD pipeline that MZ-700's
/// <see cref="Cassette"/> already runs. Until then this class just
/// satisfies IMachine's <see cref="CassetteTrapBase"/> surface so
/// MainForm's TAPE chip and auto-load orchestrator can talk to it
/// uniformly.
///
/// Follows <see cref="Mz80aCassette"/>'s shape (trap-at-vectors,
/// typed Memory property) since MZ-80A already established the
/// pattern for a lean CassetteTrapBase subclass.
/// </summary>
public sealed class Mz800Cassette : CassetteTrapBase
{
    public const ushort TrapRdInf = 0x0027;
    public const ushort TrapRdDat = 0x002A;

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
            Pending = null;
            return true;
        }
        return false;
    }
}
