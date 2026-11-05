using System;
using System.IO;
using Z80Core;

namespace MZRaku.Core;

/// <summary>
/// Shared LOAD-trap plumbing for the three Sharp cassette classes
/// (<see cref="Mz700Cassette"/> on MZ-700, <see cref="Mz80aCassette"/>
/// on MZ-80A, <see cref="Mz800Cassette"/> on MZ-800). Each machine's
/// tape routines expose distinct entry
/// points but converge on the same pattern:
/// <list type="bullet">
///   <item>A <see cref="MzfImage"/> is queued via <see cref="Queue"/>.</item>
///   <item>When the CPU's PC hits a machine-specific trap address
///     (MZ-700 and MZ-800: $04D8 / $04F8; MZ-80A: $0027 / $002A) the trap
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
/// v1.2 audit F-025 hoisted this shape here. The concrete classes
/// add their own BASIC tape traps on top.
///
/// SAVE is shared too (v1.5 4.6): a machine's write trap hands each
/// block it takes to <see cref="TakeSaveBlock"/>, and a header block
/// followed by a data block becomes a .mzf in
/// <see cref="SaveDirectory"/>, reported through <see cref="OnSaved"/>.
/// </summary>
public abstract class CassetteTrapBase
{
    internal const ushort HeaderBufferAddr = 0x10F0;
    internal const int HeaderSize = 128;

    // All three machines use Z80Core's IMemory for reads/writes at this
    // layer. Concrete classes still hold a typed field for
    // per-machine members (Mz700Memory.RomEnabled, etc.); the base
    // just needs the read/write surface.
    protected IMemory Mem = null!;
    internal Z80Cpu Cpu = null!;

    public MzfImage? Pending;
    internal bool HeaderDelivered;
    internal bool DataDelivered;

    // Counters tick each time OnPreStep injects header / body.
    // Consumed by the front end's TAPE activity chip to flash on
    // trap-hit deltas.
    public int HeaderTrapHits;
    public int DataTrapHits;

    /// <summary>
    /// Raised on both trap-driven LOADs and <see cref="DirectInject"/>
    /// with a short "Loaded / Injected: name load=… exec=… size=…"
    /// message. EmulatorSession passes it on as a status message.
    /// </summary>
    internal event Action<string>? OnLoaded;

    public virtual void Queue(MzfImage image)
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
    public virtual void ResetTrapState()
    {
        Pending = null;
        HeaderDelivered = false;
        DataDelivered = false;
        ResetSaveState();
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

    // ---- SAVE -------------------------------------------------------------

    /// <summary>Counts .mzf files written by SAVE; the front end's TAPE
    /// chip flashes when it changes.</summary>
    public int WriteTapeTrapHits;

    /// <summary>
    /// Raised when a SAVE writes a .mzf ("Saved via tape trap: …") or
    /// fails. EmulatorSession passes it on as a status message.
    /// </summary>
    internal event Action<string>? OnSaved;

    /// <summary>Where SAVE'd cassettes land: saves/ next to the exe.</summary>
    internal string SaveDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "saves");

    // The header block of the SAVE in progress, until its data block
    // arrives. Cleared on machine reset.
    private byte[]? _saveHeader;

    /// <summary>Forget a SAVE's header block that never got its data
    /// block. Called by the machine reset path.</summary>
    internal void ResetSaveState()
    {
        _saveHeader = null;
    }

    /// <summary>
    /// One block of a SAVE, taken from a machine's tape-write routine:
    /// the header block is kept, and the data block that follows
    /// completes a .mzf in <see cref="SaveDirectory"/>.
    /// </summary>
    protected void TakeSaveBlock(bool header, byte[] block)
    {
        if (header)
            _saveHeader = block;
        else if (_saveHeader == null)
            OnSaved?.Invoke("Save skipped: a data block arrived without a header.");
        else
        {
            WriteSavedTape(_saveHeader, block);
            _saveHeader = null;
        }
    }

    /// <summary>
    /// Write a SAVE's header and data blocks as a .mzf. The header is
    /// padded or cut to 128 bytes, as on tape. The file is named after
    /// the header's filename, with -1, -2… added rather than overwrite.
    /// </summary>
    private void WriteSavedTape(byte[] headerBlock, byte[] data)
    {
        var header = new byte[HeaderSize];
        Array.Copy(headerBlock, header, Math.Min(headerBlock.Length, HeaderSize));
        ushort loadAddr = (ushort)(header[0x14] | (header[0x15] << 8));

        // Header filename: ASCII at byte 1, 0x0D terminator.
        int nameLen = 0;
        while (nameLen < 16 &&
               header[1 + nameLen] != 0x0D &&
               header[1 + nameLen] != 0x00)
            nameLen++;
        var name = System.Text.Encoding.ASCII.GetString(header, 1, nameLen).Trim();
        if (string.IsNullOrWhiteSpace(name)) name = "UNNAMED";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        string finalPath;
        try
        {
            Directory.CreateDirectory(SaveDirectory);
            // Don't silently overwrite — pick a numbered suffix if the
            // base name is already used. Reproducible runs would
            // otherwise lose the previous capture without trace.
            finalPath = Path.Combine(SaveDirectory, name + ".mzf");
            int n = 1;
            while (File.Exists(finalPath))
                finalPath = Path.Combine(SaveDirectory, $"{name}-{n++}.mzf");

            using var fs = File.Create(finalPath);
            fs.Write(header, 0, HeaderSize);
            fs.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            OnSaved?.Invoke($"Save failed: {ex.Message}");
            return;
        }
        WriteTapeTrapHits++;
        OnSaved?.Invoke($"Saved via tape trap: {Path.GetFileName(finalPath)} ({data.Length} bytes from ${loadAddr:X4})");
    }
}
