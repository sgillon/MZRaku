using System;
using System.Collections.Generic;
using System.IO;
using Z80Core;

namespace MZ700Emul.Hardware;

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
/// The monitor 1Z-013A tape routines we hook:
///   $0436 = RDTAPE (reads header into $10F0, 128 bytes)
///   $04D8 = read data (uses parameters in header at $10F0 to read into RAM)
///
/// Trap semantics: when CPU PC enters one of the trap addresses AND an MZF is
/// queued, we:
///   - Write header bytes into RAM $10F0..$116F (if not yet written)
///   - Write data bytes into RAM at load_addr..load_addr+size-1 (for $04D8)
///   - Pop the return address from the stack and set PC to it
///   - Clear carry flag (success)
///   - Re-enable interrupts (EI state) since tape routines disable them
/// </summary>
public sealed class Cassette
{
    public const ushort TrapReadHeader = 0x0436;
    public const ushort TrapReadData = 0x04D8;
    public const ushort TrapBreakWait = 0x02C8;
    public const ushort HeaderBufferAddr = 0x10F0;

    // S-BASIC's outgoing tape header lives at $0FFC — cleverly tucked
    // into the $0000-$0FFF region that's RAM when BASIC has the monitor
    // ROM banked out. Layout is the standard MZF: type at +0, filename
    // at +1 (terminated by $0D), size LE at +$12, load LE at +$14.
    // Discovered empirically by dumping RAM during a SAVE attempt and
    // spotting the filename "VARTEST" at $0FFD-$1003.
    public const ushort BasicSaveHeaderAddr = 0x0FFC;

    // SAVE-tape trap address: the "press RECORD and PLAY" wait loop
    // inside the monitor's tape-write routine. This is the SAVE
    // counterpart to <see cref="TrapBreakWait"/> at $02C8 used by LOAD.
    // Found empirically: the SAVE prompt was reproduced in S-BASIC and
    // the debugger paused at PC=$0D47 while the prompt was on screen.
    // Trapping here lets us short-circuit the wait, snapshot the header
    // BASIC has prepared at $10F0, snapshot the data from
    // [load..load+size], and write a .mzf file in one go.
    public const ushort TrapWriteTape = 0x0D47;

    public sealed class MzfImage
    {
        public byte[] Header = new byte[128];
        public byte[] Data = Array.Empty<byte>();
        public string Filename = "";
        public ushort Size;
        public ushort LoadAddr;
        public ushort ExecAddr;
        public byte Type;
    }

    public MzfImage? Pending;
    public bool HeaderDelivered;
    public bool DataDelivered;
    public int BreakWaitTrapHits;
    public int HeaderTrapHits;
    public int DataTrapHits;
    public int WriteTapeTrapHits;

    // Once a SAVE has been committed within an attempt, ignore further
    // hits on the wait-loop trap. Without this we'd write the same
    // .mzf dozens of times as BASIC re-enters the wait. Cleared on
    // machine reset and on any new BasicSaveHeaderAddr filename.
    private bool _saveCommittedThisAttempt;

    public MZ700Memory Memory = null!;
    public Z80Cpu Cpu = null!;
    public Keyboard Keyboard = null!;

    public event Action<string>? OnLoaded;
    public event Action<string>? OnSaved;

    // Where SAVE'd cassettes land. Set by MainForm at startup.
    public string SaveDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "saves");

    public static MzfImage Parse(byte[] bytes)
    {
        if (bytes.Length < 128) throw new InvalidDataException("MZF too short (<128 bytes)");
        var img = new MzfImage();
        Array.Copy(bytes, img.Header, 128);
        img.Type = img.Header[0];
        img.Size = (ushort)(img.Header[0x12] | (img.Header[0x13] << 8));
        img.LoadAddr = (ushort)(img.Header[0x14] | (img.Header[0x15] << 8));
        img.ExecAddr = (ushort)(img.Header[0x16] | (img.Header[0x17] << 8));
        int nameLen = 0;
        while (nameLen < 16 && img.Header[1 + nameLen] != 0x0D && img.Header[1 + nameLen] != 0x00) nameLen++;
        // MZF filenames are stored as plain ASCII (verified by inspection of
        // multiple commercial images). Non-ASCII bytes — typically Japanese
        // katakana on Sharp's original Japanese-language software — show as
        // '?' from the ASCII encoding's default fallback.
        img.Filename = System.Text.Encoding.ASCII.GetString(img.Header, 1, nameLen);
        int dataLen = Math.Min(img.Size, Math.Max(0, bytes.Length - 128));
        img.Data = new byte[dataLen];
        if (dataLen > 0) Array.Copy(bytes, 128, img.Data, 0, dataLen);
        return img;
    }

    public void Queue(MzfImage image)
    {
        Pending = image;
        HeaderDelivered = false;
        DataDelivered = false;
    }

    /// <summary>
    /// Directly inject MZF into RAM and jump to its execution address.
    /// Used for auto-load at startup where we don't want to go through
    /// the monitor's LOAD command. Must be called while CPU is halted.
    /// </summary>
    public void DirectInject(MzfImage img, bool jumpExec = true)
    {
        for (int i = 0; i < 128; i++)
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
    public void FixupBasicProgramPointers(ushort startAddr, int dataLen)
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
    public bool OnPreStep()
    {
        ushort pc = Cpu.PC;

        // SAVE-tape trap lives in S-BASIC's own code (high-RAM region),
        // so it must run with ROM banked OUT — the opposite of the other
        // traps. Specifically NOT gated by Memory.RomEnabled. Found
        // empirically by pausing the debugger during a SAVE prompt.
        //
        // We do NOT pop the stack or change PC: $0D47 is mid-routine
        // (not the entry), so the stack at this point has saved
        // registers from the routine's own PUSHes, not a clean return
        // address. Best we can do for now is snapshot the .mzf once
        // and let BASIC stay in its wait loop — the user resets to
        // escape. Properly exiting the wait needs disassembly of the
        // routine around $0D40-$0D60 to find the post-wait address.
        if (pc == TrapWriteTape && !Memory.RomEnabled)
        {
            WriteTapeTrapHits++;
            if (!_saveCommittedThisAttempt)
            {
                CommitSavedTape();
                _saveCommittedThisAttempt = true;
            }
            return false;
        }

        // Re-arm the SAVE trap once BASIC has gone back to executing
        // monitor ROM code (i.e. the Ready prompt's keyboard/display
        // calls). Without this the BREAK-on-save short-circuit would
        // also fire on the next SAVE before any data has been written.
        if (_saveCommittedThisAttempt && Memory.RomEnabled && pc < 0x1000)
            _saveCommittedThisAttempt = false;

        if (!Memory.RomEnabled) return false;
        if (pc == TrapBreakWait)
        {
            // Monitor's "press PLAY / check BREAK" wait. With no physical tape
            // and large PIT periods, the natural timeout can take many seconds
            // under our emulation. Short-circuit it, returning CY=1 if the
            // user is actually holding the BREAK key (matrix (8,5), driven
            // by PC Esc via SpecialKeyMap) and CY=0 otherwise — that's what
            // a real MZ-700 would report on a normal LOAD with no break held,
            // and what BASIC needs to honour the user's Esc when polling for
            // break during a RUN.
            //
            // Special case: once a SAVE has been committed, force CY=1 so
            // BASIC abandons its tape-write loop and returns to Ready —
            // there's no real tape to acknowledge so it would otherwise
            // generate tape signal forever.
            BreakWaitTrapHits++;
            bool breakHeld = _saveCommittedThisAttempt || IsBreakHeld();
            if (breakHeld)
                Cpu.F |= 0x01;
            else
                Cpu.F &= 0xFE;
            Cpu.PC = PopFromStack();
            return true;
        }
        if (Pending == null) return false;
        if (pc == TrapReadHeader && !HeaderDelivered)
        {
            HeaderTrapHits++;
            // $0436: reads 128-byte header into $10F0. Entry has not yet pushed anything.
            for (int i = 0; i < 128; i++)
                Memory.Write((ushort)(HeaderBufferAddr + i), Pending.Header[i]);
            HeaderDelivered = true;
            // Return with CY=0 (success)
            Cpu.F &= 0xFE;
            // Pop return address from stack (the CPU's PC should go to the caller)
            Cpu.PC = PopFromStack();
            Cpu.IFF1 = Cpu.IFF2 = true; // tape routines end with EI normally
            return true;
        }
        if (pc == TrapReadData && !DataDelivered)
        {
            DataTrapHits++;
            // $04D8: reads data using header at $10F0
            // Ensure header is present in memory
            if (!HeaderDelivered)
            {
                for (int i = 0; i < 128; i++)
                    Memory.Write((ushort)(HeaderBufferAddr + i), Pending.Header[i]);
                HeaderDelivered = true;
            }
            ushort loadAddr = (ushort)(Memory.Read(HeaderBufferAddr + 0x14) | (Memory.Read(HeaderBufferAddr + 0x15) << 8));
            ushort size = (ushort)(Memory.Read(HeaderBufferAddr + 0x12) | (Memory.Read(HeaderBufferAddr + 0x13) << 8));
            int n = Math.Min(size, Pending.Data.Length);
            for (int i = 0; i < n; i++)
                Memory.Write((ushort)(loadAddr + i), Pending.Data[i]);
            DataDelivered = true;
            Cpu.F &= 0xFE;
            Cpu.PC = PopFromStack();
            Cpu.IFF1 = Cpu.IFF2 = true;

            OnLoaded?.Invoke($"Loaded via tape trap: {Pending.Filename} ({n} bytes at ${loadAddr:X4})");

            // After full load, clear pending (unless it's a BASIC program being LOADed
            // into BASIC - in that case we still clear; next LOAD will queue another MZF).
            Pending = null;
            HeaderDelivered = false;
            DataDelivered = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Re-arm the SAVE-tape trap so the next SAVE attempt fires. Called
    /// by the machine reset path.
    /// </summary>
    public void ResetSaveState()
    {
        _saveCommittedThisAttempt = false;
    }

    // BREAK lives on matrix (8, 7). Active low, so a held key reads as
    // 0 in that bit. SpecialKeyMap binds Esc to this position; this
    // helper is what makes the BreakWait trap honour it.
    private bool IsBreakHeld() => (Keyboard.ReadRow(8) & (1 << 7)) == 0;

    /// <summary>
    /// One-shot diagnostic that writes the bytes at $0D40-$0D7F (BASIC's
    /// wait code, since ROM is banked out when this is called) into a
    /// text file in the save directory. Used to figure out the proper
    /// exit address for the SAVE wait loop.
    /// </summary>
    private void DumpBasicWaitCode()
    {
        try
        {
            Directory.CreateDirectory(SaveDirectory);
            var path = Path.Combine(SaveDirectory, "basic_wait_code.txt");
            using var w = new StreamWriter(path);
            w.WriteLine("; BASIC code at $0D40-$0D7F captured at SAVE-trap time (ROM banked out).");
            w.WriteLine($"; PC=${Cpu.PC:X4} SP=${Cpu.SP:X4} AF=${Cpu.AF:X4} BC=${Cpu.BC:X4} DE=${Cpu.DE:X4} HL=${Cpu.HL:X4}");
            w.WriteLine();
            for (int row = 0x0D40; row < 0x0D80; row += 16)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"{row:X4}: ");
                for (int i = 0; i < 16; i++)
                    sb.Append($"{Memory.Read((ushort)(row + i)):X2} ");
                sb.Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    byte b = Memory.Read((ushort)(row + i));
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                w.WriteLine(sb.ToString());
            }
            // Also capture a wider stack snapshot so we can spot the real
            // return address in there.
            w.WriteLine();
            w.WriteLine($"; Stack contents at SP=${Cpu.SP:X4} (16 bytes):");
            var sbs = new System.Text.StringBuilder();
            sbs.Append($"{Cpu.SP:X4}: ");
            for (int i = 0; i < 16; i++)
                sbs.Append($"{Memory.Read((ushort)(Cpu.SP + i)):X2} ");
            w.WriteLine(sbs.ToString());
        }
        catch { /* non-fatal */ }
    }

    private ushort PopFromStack()
    {
        byte lo = Memory.Read(Cpu.SP); Cpu.SP++;
        byte hi = Memory.Read(Cpu.SP); Cpu.SP++;
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>
    /// Read the 128-byte header BASIC has prepared at $0FFC, snapshot
    /// the data from [load..load+size], and write a .mzf file. Called
    /// from inside the SAVE-wait trap (ROM banked out, so $0FFC..$0FFF
    /// reads as RAM); pop+return is the caller's job.
    /// </summary>
    private void CommitSavedTape()
    {
        var header = new byte[128];
        for (int i = 0; i < 128; i++)
            header[i] = Memory.Read((ushort)(BasicSaveHeaderAddr + i));

        ushort size = (ushort)(header[0x12] | (header[0x13] << 8));
        ushort loadAddr = (ushort)(header[0x14] | (header[0x15] << 8));

        if (size == 0)
        {
            OnSaved?.Invoke($"Save skipped: header at ${BasicSaveHeaderAddr:X4} has size=0 (not populated).");
            return;
        }

        var data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = Memory.Read((ushort)(loadAddr + i));

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
            fs.Write(header, 0, 128);
            fs.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            OnSaved?.Invoke($"Save failed: {ex.Message}");
            return;
        }
        OnSaved?.Invoke($"Saved via tape trap: {Path.GetFileName(finalPath)} ({data.Length} bytes from ${loadAddr:X4})");
    }
}
