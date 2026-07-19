using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Sharp MZ-80A cassette-tape trap harness. SA-1510 exposes tape I/O
/// via well-defined jump-table entries per Owner's Manual §2.1.2:
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
/// The <see cref="Cassette.MzfImage"/> record from MZ-700 is reused —
/// .mzf files are Sharp-family shared and the parser is identical.
///
/// Phase 4 wires the two READ traps and <see cref="DirectInject"/>
/// (bypasses the monitor's LOAD entirely). Write traps (SAVE) can
/// layer on later if needed.
/// </summary>
public sealed class Mz80aCassette
{
    public MZ80AMemory Memory = null!;
    public Z80Cpu Cpu = null!;

    public const ushort TrapRdInf = 0x0027;
    public const ushort TrapRdDat = 0x002A;
    public const ushort HeaderBufferAddr = 0x10F0;
    public const int HeaderSize = 128;

    public Cassette.MzfImage? Pending;
    public bool HeaderDelivered;
    public bool DataDelivered;

    // Counters tick each time OnPreStep injects header / body. Consumed
    // by MainForm's TAPE activity chip to flash on trap-hit deltas; keep
    // in step with Cassette.HeaderTrapHits / DataTrapHits on MZ-700.
    public int HeaderTrapHits;
    public int DataTrapHits;

    public event Action<string>? OnLoaded;

    public void Queue(Cassette.MzfImage img)
    {
        Pending = img;
        HeaderDelivered = false;
        DataDelivered = false;
    }

    public void ResetTrapState()
    {
        Pending = null;
        HeaderDelivered = false;
        DataDelivered = false;
    }

    /// <summary>
    /// Write the header + body straight into memory and (optionally)
    /// jump to the image's exec address. Bypasses the SA-1510 LOAD
    /// dispatch entirely — useful for auto-loading BASIC before the
    /// keyboard input path is fully in place, or for machine-code
    /// programs the user drags onto the window.
    /// </summary>
    public void DirectInject(Cassette.MzfImage img, bool jumpExec = true)
    {
        for (int i = 0; i < HeaderSize; i++)
            Memory.Write((ushort)(HeaderBufferAddr + i), img.Header[i]);
        for (int i = 0; i < img.Data.Length; i++)
            Memory.Write((ushort)(img.LoadAddr + i), img.Data[i]);
        if (jumpExec)
        {
            Cpu.PC = img.ExecAddr;
        }
        OnLoaded?.Invoke($"Injected: {img.Filename} load=${img.LoadAddr:X4} exec=${img.ExecAddr:X4} size={img.Data.Length}");
    }

    /// <summary>
    /// Replicate SA-5510's post-LOAD workspace pointer updates so
    /// DirectInject can stand in for a user-typed LOAD command.
    /// Discovered empirically 2026-07-12 via pre/post-LOAD RAM diff on
    /// cricket.mzf: LOAD's only load-bearing change is a 36-entry
    /// pointer table at $4E4E-$4E95, where entry[i] holds
    /// ProgramEnd + 2*i (entry 0 at $4E4E is VARTAB itself). Other
    /// diff regions are cosmetic (command-echo buffer, line-input
    /// state, Z80 stack scraps) and can be ignored.
    /// </summary>
    public void FixupBasicProgramPointers(ushort loadAddr, int dataLen)
    {
        int programEnd = loadAddr + dataLen;
        // The $4E4E variable-slot table: 36 pointers stepping by 2,
        // rooted at ProgramEnd (VARTAB). Discovered 2026-07-12 via
        // pre/post-LOAD RAM diff.
        for (int i = 0; i < 36; i++)
        {
            int addr = 0x4E4E + i * 2;
            int val  = programEnd + i * 2;
            Memory.Write((ushort)addr, (byte)(val & 0xFF));
            Memory.Write((ushort)(addr + 1), (byte)((val >> 8) & 0xFF));
        }
    }

    /// <summary>
    /// Traps SA-1510's tape read entry points when a Pending image is
    /// queued. Injects header / data into RAM, synthesises a
    /// successful RET (CY=0), then advances state so a follow-up call
    /// picks up the next stage.
    /// </summary>
    public bool OnPreStep()
    {
        if (Pending == null) return false;
        ushort pc = Cpu.PC;
        if (pc == TrapRdInf && !HeaderDelivered)
        {
            HeaderTrapHits++;
            for (int i = 0; i < HeaderSize; i++)
                Memory.Write((ushort)(HeaderBufferAddr + i), Pending.Header[i]);
            HeaderDelivered = true;
            SynthesiseSuccess();
            return true;
        }
        if (pc == TrapRdDat && HeaderDelivered && !DataDelivered)
        {
            DataTrapHits++;
            for (int i = 0; i < Pending.Data.Length; i++)
                Memory.Write((ushort)(Pending.LoadAddr + i), Pending.Data[i]);
            DataDelivered = true;
            SynthesiseSuccess();
            // One-shot — clear pending so a second L command won't
            // re-inject the same image.
            Pending = null;
            return true;
        }
        return false;
    }

    private void SynthesiseSuccess()
    {
        // C flag = 0 (bit 0 of F) signals success to the caller.
        Cpu.F &= 0xFE;
        // Pop the return address off the stack and jump there.
        byte lo = Memory.Read(Cpu.SP);
        byte hi = Memory.Read((ushort)(Cpu.SP + 1));
        Cpu.SP += 2;
        Cpu.PC = (ushort)((hi << 8) | lo);
    }
}
