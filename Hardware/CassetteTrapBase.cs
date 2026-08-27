using System;
using Z80Core;

namespace MZRaku.Hardware;

/// <summary>
/// Shared LOAD-trap plumbing for the two Sharp cassette classes
/// (<see cref="Cassette"/> on MZ-700, <see cref="Mz80aCassette"/>
/// on MZ-80A). Both machines' tape routines expose distinct entry
/// points but converge on the same pattern:
/// <list type="bullet">
///   <item>A <see cref="MzfImage"/> is queued via <see cref="Queue"/>.</item>
///   <item>When the CPU's PC hits a machine-specific trap address
///     (MZ-700: $0436 / $04D8; MZ-80A: $0027 / $002A) the trap
///     injects header or data into RAM at
///     <see cref="HeaderBufferAddr"/> / the header's load address,
///     synthesises a successful RET (CY=0, pop the return address
///     off the stack, jump there), and marks the appropriate stage
///     delivered.</item>
///   <item>Once both stages have fired, <see cref="Pending"/> is
///     cleared so a second read command doesn't re-inject the
///     same image.</item>
/// </list>
///
/// v1.2 audit F-025 hoisted this shape here. The concrete
/// <see cref="Cassette"/> keeps its BreakWait short-circuit + SAVE
/// trap machinery + $1170 mirror hooks on top of the base; MZ-80A
/// only extends the base with its two trap-address checks. MZ-700
/// SAVE handling stays MZ-700-only — SA-1510 doesn't expose a
/// wait-loop trap on the WRITE side that would benefit from the
/// same treatment.
/// </summary>
public abstract class CassetteTrapBase
{
    public const ushort HeaderBufferAddr = 0x10F0;
    public const int HeaderSize = 128;

    // Both machines use Z80Core's IMemory for reads/writes at this
    // layer. Concrete classes still hold a typed field for
    // per-machine members (MZ700Memory.RomEnabled, etc.); the base
    // just needs the read/write surface.
    protected IMemory Mem = null!;
    public Z80Cpu Cpu = null!;

    public MzfImage? Pending;
    public bool HeaderDelivered;
    public bool DataDelivered;

    // Counters tick each time OnPreStep injects header / body.
    // Consumed by MainForm's TAPE activity chip to flash on
    // trap-hit deltas.
    public int HeaderTrapHits;
    public int DataTrapHits;

    /// <summary>
    /// Raised on both trap-driven LOADs and <see cref="DirectInject"/>
    /// with a short "Loaded / Injected: name load=… exec=… size=…"
    /// message. MainForm subscribes to update the status label.
    /// </summary>
    public event Action<string>? OnLoaded;

    public void Queue(MzfImage image)
    {
        Pending = image;
        HeaderDelivered = false;
        DataDelivered = false;
    }

    /// <summary>
    /// Clear queued cassette state — pending image + both delivery
    /// flags. Called from the machine reset path so a Pending image
    /// doesn't get served to a freshly-booting monitor's tape traps.
    /// </summary>
    public void ResetTrapState()
    {
        Pending = null;
        HeaderDelivered = false;
        DataDelivered = false;
    }

    /// <summary>
    /// Directly inject MZF into RAM and (optionally) jump to its
    /// execution address. Used for auto-load at startup where we
    /// don't want to go through the monitor's LOAD command. Must be
    /// called while CPU is halted or about to run from a
    /// well-defined state (typically post-monitor-ready).
    /// </summary>
    public virtual void DirectInject(MzfImage img, bool jumpExec = true)
    {
        for (int i = 0; i < HeaderSize; i++)
            Mem.Write((ushort)(HeaderBufferAddr + i), img.Header[i]);
        for (int i = 0; i < img.Data.Length; i++)
            Mem.Write((ushort)(img.LoadAddr + i), img.Data[i]);
        if (jumpExec)
            Cpu.PC = img.ExecAddr;
        RaiseLoaded($"Injected: {img.Filename} load=${img.LoadAddr:X4} exec=${img.ExecAddr:X4} size={img.Data.Length}");
    }

    /// <summary>
    /// Called before the CPU fetches an instruction. Concrete
    /// subclasses check their trap addresses and dispatch. Returns
    /// true if the handler advanced PC (skip the natural fetch).
    /// </summary>
    public abstract bool OnPreStep();

    /// <summary>
    /// Injects the pending image's 128-byte header into RAM at
    /// <see cref="HeaderBufferAddr"/>. Idempotent — subsequent
    /// calls with <see cref="HeaderDelivered"/> already set skip
    /// the write.
    /// </summary>
    protected void WriteHeaderToBuffer()
    {
        if (Pending is null) return;
        for (int i = 0; i < HeaderSize; i++)
            Mem.Write((ushort)(HeaderBufferAddr + i), Pending.Header[i]);
    }

    /// <summary>
    /// Injects the pending image's data payload at
    /// <paramref name="loadAddr"/>. <paramref name="size"/> is the
    /// header-declared size; the actual write clamps to whatever's
    /// in <see cref="Pending"/>'s Data array so truncated .mzf
    /// files don't over-read.
    /// </summary>
    protected int WriteDataToRam(ushort loadAddr, int size)
    {
        if (Pending is null) return 0;
        int n = Math.Min(size, Pending.Data.Length);
        for (int i = 0; i < n; i++)
            Mem.Write((ushort)(loadAddr + i), Pending.Data[i]);
        return n;
    }

    /// <summary>
    /// Synthesise the tape-routine success return: clear the C
    /// flag (CY=0 signals success on both SA-1510 and 1Z-013A),
    /// pop the return address off the stack, jump there.
    /// Concrete subclasses call this from their per-trap
    /// handlers; MZ-700 additionally forces IFF1/IFF2 back on
    /// because 1Z-013A's tape routines end with EI.
    /// </summary>
    protected void SynthesiseSuccess()
    {
        Cpu.F &= 0xFE;
        Cpu.PC = PopFromStack();
    }

    protected ushort PopFromStack()
    {
        byte lo = Mem.Read(Cpu.SP); Cpu.SP++;
        byte hi = Mem.Read(Cpu.SP); Cpu.SP++;
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>
    /// Concrete subclasses call this to raise the shared
    /// <see cref="OnLoaded"/> event with the standard status
    /// message shape. Kept as a protected wrapper so the event
    /// stays sealed at the base — external subscribers don't
    /// depend on concrete-class-specific state.
    /// </summary>
    protected void RaiseLoaded(string message) => OnLoaded?.Invoke(message);
}
