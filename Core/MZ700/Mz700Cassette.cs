using System;
using System.Collections.Generic;
using System.IO;
using Z80Core;

namespace MZRaku.Core;

/// <summary>
/// MZF cassette image loader + monitor tape-routine trap.
///
/// MZF file format:
///   offset 0      : attribute/type (01=MC, 02=MZ-80 BASIC, 05=MZ-700 BASIC, etc.)
///   offset 1..17  : filename (up to 16 chars, 0x0D terminator)
///   offset 18..19 : size (LE)
///   offset 20..21 : load address (LE)
///   offset 22..23 : execution address (LE)
///   offset 24..127: comment / padding
///   offset 128... : raw file data (size bytes)
///
/// The monitor 1Z-013A tape routines we hook (its jump table sends
/// RDINF $0027 to $04D8 and RDDAT $002A to $04F8):
///   $04D8 = RDINF (reads the 128-byte header into $10F0)
///   $04F8 = RDDAT (uses the header at $10F0 to read the data into RAM)
/// $0436 is WRINF, the header *write*; trapping it as the header read
/// (before v1.5) sent the monitor's L to the data trap, which emptied
/// the tape before RDDAT ran.
///
/// Trap semantics: when CPU PC enters one of the trap addresses AND an MZF is
/// queued, we:
///   - Write header bytes into RAM $10F0..$116F (if not yet written)
///   - Write data bytes into RAM at load_addr..load_addr+size-1 (for $04F8)
///   - Pop the return address from the stack and set PC to it
///   - Clear carry flag (success)
///   - Re-enable interrupts (EI state) since tape routines disable them
/// </summary>
public sealed class Mz700Cassette : CassetteTrapBase
{
    internal const ushort TrapReadHeader = 0x04D8;   // RDINF
    internal const ushort TrapReadData = 0x04F8;     // RDDAT

    // S-BASIC 1Z-013B's own tape routines, used with the monitor ROM
    // banked out. BASIC keeps a jump table at the monitor's vectors:
    //   $0021 -> $0A3B write header (E=$CC)   $0027 -> $0A7A read header
    //   $0024 -> $0A3F write data   (E=$53)   $002A -> $0A7E read data
    //   $002D -> $0A94 verify
    // Each takes HL = buffer and BC = byte count; reads and writes exit
    // through $0A71 (POP DE, clear the flag at $0050, RET with CY=0 on
    // success; the motor-off routine before it ends with EI). Found
    // 2026-10-31 (v1.5 Phases 2.5 / 2.6) from the call chain while LOAD
    // waited for a tape. The bytes at each trap are checked first, so a
    // different BASIC is left alone.
    internal const ushort TrapBasicReadHeader = 0x0A7A;
    internal const ushort TrapBasicReadData = 0x0A7E;
    private static readonly byte[] BasicReadHeaderEntry = { 0x3E, 0xCC, 0x18, 0x02 };   // LD A,$CC; JR $0A80
    private static readonly byte[] BasicReadDataEntry = { 0x3E, 0x53, 0xD5, 0x16, 0xD2 };   // LD A,$53; PUSH DE; LD D,$D2

    // SAVE: the write body at $0A67, where both writes meet once BASIC
    // has printed "WRITING <name>" and passed the motor check, just
    // before it writes the tape mark and the block. DE pushed at $0A41
    // is the only thing above the return address. E says which block
    // ($CC header, $53 data); HL and BC are still the caller's.
    internal const ushort TrapBasicWrite = 0x0A67;
    private static readonly byte[] BasicWriteBody = { 0xF3, 0xCD, 0x5A, 0x0B, 0xD4, 0x85, 0x0B };   // DI; CALL $0B5A; CALL NC,$0B85
    private const ushort BasicTapeFlagAddr = 0x0050;

    public int WriteTapeTrapHits;

    // The header block of the SAVE in progress, until its data block
    // arrives. Cleared on machine reset.
    private byte[]? _saveHeader;

    // MZ-700-specific typed handle to the memory; the base holds it as
    // Z80Core.IMemory (Mem) for the shared read/write path, but
    // RomEnabled + the SAVE trap's raw RAM peek need the concrete type.
    private Mz700Memory _mem = null!;
    internal Mz700Memory Memory
    {
        get => _mem;
        set { _mem = value; Mem = value; }
    }

    internal event Action<string>? OnSaved;

    // Where SAVE'd cassettes land: saves/ next to the exe.
    internal string SaveDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "saves");

    /// <summary>
    /// MZ-700 override: write payload straight to physical Ram[] and bank
    /// VRAM out if the program spills past $CFFF. The base's Mem.Write
    /// routes writes to $D000-$DFFF into Vram/Aram when VramIoEnabled is
    /// true — so a BASIC program larger than ~25 KB has its tail land in
    /// the character/attribute VRAM instead of extended RAM, corrupting
    /// the screen and truncating what RUN can read back. Real S-BASIC's
    /// LOAD banks VRAM out (OUT ($E1),A → VramIoEnabled=false) before
    /// calling the monitor's tape read and leaves it out so RUN can walk
    /// the program through $D000-$DFFF; we mimic that here. Discovered
    /// 2026-08-27 via Dragon Caves (29,283 bytes @ $6BCF → extends to
    /// $DE32); the bug appeared intermittent because BASIC's post-Ready
    /// idle loop toggles VramIoEnabled and DirectInject's 60-frame wait
    /// caught random points in that cycle.
    /// </summary>
    public override void DirectInject(MzfImage img, bool jumpExec = true)
    {
        for (int i = 0; i < HeaderSize; i++)
            _mem.Ram[HeaderBufferAddr + i] = img.Header[i];
        for (int i = 0; i < img.Data.Length; i++)
            _mem.Ram[img.LoadAddr + i] = img.Data[i];
        if (img.LoadAddr + img.Data.Length > 0xD000)
            _mem.VramIoEnabled = false;
        if (jumpExec)
            Cpu.PC = img.ExecAddr;
        RaiseLoaded($"Injected: {img.Filename} load=${img.LoadAddr:X4} exec=${img.ExecAddr:X4} size={img.Data.Length}");
    }

    /// <summary>
    /// Update S-BASIC's program-area control block after a cassette program
    /// has been direct-injected at <paramref name="startAddr"/>.
    ///
    /// S-BASIC's LIST walks the program by on-disk LENGTH fields (the leading
    /// 2 bytes of each line record), not by absolute next-pointers — so we do
    /// NOT rewrite the line-record headers. RUN runs its own preprocess that
    /// converts lengths to next-pointers in-place, walking until it reaches
    /// the address held in S-BASIC's VARTAB-equivalent cluster at $6AB3.
    ///
    /// Two clusters of pointers need updating:
    ///   $6ABF-$6AC0 = TXTTAB (start of program text). Default $6BCF.
    ///   $6AB3-$6AB8 = three end-of-program pointers (VARTAB family).
    ///
    /// np.mzf works without TXTTAB updates because its load address ($6BCF)
    /// happens to match the default; trek.mzf at $6BDF needs TXTTAB shifted
    /// to find its program. After loading we point TXTTAB at startAddr and
    /// set all three end pointers one byte past the $00 $00 program-end
    /// marker so RUN's preprocessor walks the full program. The cluster
    /// addresses were found by searching the BASIC image for the literal
    /// $6BCF default value (one occurrence at post-relocation RAM $6ABF)
    /// and other pointers near it.
    /// </summary>
    internal void FixupBasicProgramPointers(ushort startAddr, int dataLen)
    {
        // TXTTAB: program text start.
        Memory.Write(0x6ABF, (byte)(startAddr & 0xFF));
        Memory.Write(0x6AC0, (byte)(startAddr >> 8));
        // VARTAB family: end-of-program pointers.
        ushort newEnd = (ushort)(startAddr + dataLen + 1);
        Memory.Write(0x6AB3, (byte)(newEnd & 0xFF));
        Memory.Write(0x6AB4, (byte)(newEnd >> 8));
        Memory.Write(0x6AB5, (byte)(newEnd & 0xFF));
        Memory.Write(0x6AB6, (byte)(newEnd >> 8));
        Memory.Write(0x6AB7, (byte)(newEnd & 0xFF));
        Memory.Write(0x6AB8, (byte)(newEnd >> 8));
    }

    /// <summary>
    /// Called before the CPU fetches an instruction. If the CPU is at a tape
    /// trap address and we have a queued MZF, perform the in-memory load and
    /// return immediately (emulating a successful tape read).
    /// Returns true if we handled the instruction (PC changed).
    ///
    /// IMPORTANT: traps only fire when the monitor ROM is actually mapped at
    /// $0000-$0FFF. S-BASIC banks ROM out at $7D94 and runs its own code from
    /// the same address range — without this guard our traps fire on BASIC
    /// instructions (e.g. $0436 is BASIC's keyboard decoder) and corrupt the
    /// stack, breaking the entire interpreter.
    /// </summary>
    public override bool OnPreStep()
    {
        ushort pc = Cpu.PC;

        // S-BASIC (ROM banked out): SAVE writes a .mzf; LOAD reads an
        // inserted cassette.
        if (!_mem.RomEnabled)
        {
            if (pc == TrapBasicWrite && EntryMatches(pc, BasicWriteBody))
                return TakeBasicWrite();
            if (Pending != null
                && ((pc == TrapBasicReadHeader && !HeaderDelivered && EntryMatches(pc, BasicReadHeaderEntry))
                 || (pc == TrapBasicReadData && !DataDelivered && EntryMatches(pc, BasicReadDataEntry))))
                return ServeBasicRead(pc == TrapBasicReadHeader);
            return false;
        }
        if (Pending == null) return false;
        if (pc == TrapReadHeader && !HeaderDelivered)
        {
            HeaderTrapHits++;
            // $04D8 RDINF: reads 128-byte header into $10F0. Entry has not yet pushed anything.
            WriteHeaderToBuffer();
            HeaderDelivered = true;
            // Return with CY=0 (success) + pop the stack + jump to
            // the caller. Tape routines end with EI on 1Z-013A, so
            // also force IFF1/IFF2 back on.
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;
            return true;
        }
        if (pc == TrapReadData && !DataDelivered)
        {
            DataTrapHits++;
            // $04F8 RDDAT: reads data using header at $10F0
            // Ensure header is present in memory
            if (!HeaderDelivered)
            {
                WriteHeaderToBuffer();
                HeaderDelivered = true;
            }
            ushort loadAddr = (ushort)(Mem.Read(HeaderBufferAddr + 0x14) | (Mem.Read(HeaderBufferAddr + 0x15) << 8));
            ushort size = (ushort)(Mem.Read(HeaderBufferAddr + 0x12) | (Mem.Read(HeaderBufferAddr + 0x13) << 8));
            int n = WriteDataToRam(loadAddr, size);
            DataDelivered = true;
            SynthesiseSuccess();
            Cpu.IFF1 = Cpu.IFF2 = true;

            RaiseLoaded($"Loaded via tape trap: {Pending!.Filename} ({n} bytes at ${loadAddr:X4})");

            // After full load, clear pending (unless it's a BASIC program being LOADed
            // into BASIC - in that case we still clear; next LOAD will queue another MZF).
            Pending = null;
            HeaderDelivered = false;
            DataDelivered = false;
            return true;
        }
        return false;
    }

    private bool EntryMatches(ushort addr, byte[] expected)
    {
        for (int i = 0; i < expected.Length; i++)
            if (_mem.Ram[(ushort)(addr + i)] != expected[i]) return false;
        return true;
    }

    /// <summary>
    /// One of S-BASIC's tape reads, served from <see cref="CassetteTrapBase.Pending"/>:
    /// BC bytes of the header (or data) into RAM at HL, then the
    /// routine's own exit. With BC = 0 the real routine returns at
    /// once, so it's left to run.
    /// </summary>
    private bool ServeBasicRead(bool header)
    {
        int count = (Cpu.B << 8) | Cpu.C;
        if (count == 0) return false;
        ushort dest = (ushort)((Cpu.H << 8) | Cpu.L);
        byte[] src = header ? Pending!.Header : Pending!.Data;
        int n = Math.Min(count, src.Length);
        // The real routine banks VRAM out around every port access and
        // leaves it out, so its stores land in RAM.
        for (int i = 0; i < n; i++)
            _mem.Ram[(ushort)(dest + i)] = src[i];
        _mem.VramIoEnabled = false;
        _mem.Ram[BasicTapeFlagAddr] = 0;
        Cpu.A = 0;
        SynthesiseSuccess();
        Cpu.IFF1 = Cpu.IFF2 = true;      // the motor-off path ends with EI
        if (header)
        {
            HeaderTrapHits++;
            HeaderDelivered = true;
        }
        else
        {
            DataTrapHits++;
            RaiseLoaded($"Loaded via BASIC tape trap: {Pending.Filename} ({n} bytes at ${dest:X4})");
            Pending = null;
            HeaderDelivered = false;
            DataDelivered = false;
        }
        return true;
    }

    /// <summary>
    /// Re-arm the SAVE-tape trap so the next SAVE attempt fires. Called
    /// by the machine reset path.
    /// </summary>
    internal void ResetSaveState()
    {
        _saveHeader = null;
    }

    /// <summary>
    /// One of S-BASIC's tape writes (SAVE), taken at the write body
    /// instead of going to tape: the header block is kept, and the data
    /// block that follows completes a .mzf in <see cref="SaveDirectory"/>.
    /// Then the routine's own exit, so BASIC carries on to Ready.
    /// </summary>
    private bool TakeBasicWrite()
    {
        ushort src = (ushort)((Cpu.H << 8) | Cpu.L);
        int count = (Cpu.B << 8) | Cpu.C;
        var block = new byte[count];
        for (int i = 0; i < count; i++)
            block[i] = _mem.Ram[(ushort)(src + i)];

        if (Cpu.E == 0xCC)
            _saveHeader = block;
        else if (_saveHeader == null)
            OnSaved?.Invoke("Save skipped: a data block arrived without a header.");
        else
        {
            WriteSavedTape(_saveHeader, block);
            _saveHeader = null;
        }

        // The exit at $0A6E/$0A71: DE back, flag cleared, CY=0, EI.
        ushort de = PopFromStack();
        Cpu.D = (byte)(de >> 8);
        Cpu.E = (byte)de;
        _mem.Ram[BasicTapeFlagAddr] = 0;
        Cpu.A = 0;
        SynthesiseSuccess();
        Cpu.IFF1 = Cpu.IFF2 = true;
        return true;
    }

    /// <summary>
    /// Write a SAVE's header and data blocks as a .mzf. The header is
    /// padded or cut to 128 bytes, as on tape.
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
