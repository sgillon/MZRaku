using System;
using System.Collections.Generic;
using System.IO;
using MZ700Emul.Z80;

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

    public MZ700Memory Memory = null!;
    public Z80Cpu Cpu = null!;

    public event Action<string>? OnLoaded;

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
        if (!Memory.RomEnabled) return false;
        ushort pc = Cpu.PC;
        if (pc == TrapBreakWait)
        {
            // Monitor's "press PLAY / check BREAK" wait. With no physical tape
            // and large PIT periods, the natural timeout can take many seconds
            // under our emulation. Always short-circuit to the "no break, no
            // timeout error" path. This is correct both for cassette LOAD and
            // for BASIC's periodic break polls.
            BreakWaitTrapHits++;
            Cpu.F &= 0xFE; // CY=0 (success / no break)
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

    private ushort PopFromStack()
    {
        byte lo = Memory.Read(Cpu.SP); Cpu.SP++;
        byte hi = Memory.Read(Cpu.SP); Cpu.SP++;
        return (ushort)(lo | (hi << 8));
    }
}
