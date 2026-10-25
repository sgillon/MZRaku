namespace MZRaku.Hardware;

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
/// class provides the SA-1510-specific trap addresses and
/// FixupBasicProgramPointers. Write traps (SAVE) can layer on
/// later if needed.
/// </summary>
public sealed class Mz80aCassette : CassetteTrapBase
{
    public const ushort TrapRdInf = 0x0027;
    public const ushort TrapRdDat = 0x002A;

    // Typed field so callers wire Memory as MZ80AMemory; base holds
    // it as IMemory (Mem) for its read/write path.
    private MZ80AMemory _mem = null!;
    public MZ80AMemory Memory
    {
        get => _mem;
        set { _mem = value; Mem = value; }
    }

    /// <summary>
    /// Replicate SA-5510's post-LOAD workspace pointer updates so
    /// DirectInject can stand in for a user-typed LOAD command.
    /// Discovered empirically 2026-07-12 via pre/post-LOAD RAM
    /// diff on cricket.mzf: LOAD's only load-bearing change is a
    /// 36-entry pointer table at $4E4E-$4E95, where entry[i] holds
    /// ProgramEnd + 2*i (entry 0 at $4E4E is VARTAB itself). Other
    /// diff regions are cosmetic (command-echo buffer, line-input
    /// state, Z80 stack scraps) and can be ignored.
    /// </summary>
    public void FixupBasicProgramPointers(ushort loadAddr, int dataLen)
    {
        int programEnd = loadAddr + dataLen;
        for (int i = 0; i < 36; i++)
        {
            int addr = 0x4E4E + i * 2;
            int val  = programEnd + i * 2;
            Mem.Write((ushort)addr, (byte)(val & 0xFF));
            Mem.Write((ushort)(addr + 1), (byte)((val >> 8) & 0xFF));
        }
    }

    /// <summary>
    /// Traps SA-1510's tape read entry points when a Pending image
    /// is queued. Injects header / data into RAM, synthesises a
    /// successful RET (CY=0), then advances state so a follow-up
    /// call picks up the next stage.
    /// </summary>
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
            // One-shot — clear pending so a second L command won't
            // re-inject the same image.
            Pending = null;
            return true;
        }
        return false;
    }
}
